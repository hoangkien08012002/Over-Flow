using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Infrastructure.Identity;

public sealed record AuthTokens(string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc, string RefreshToken);
public sealed record RegisteredUser(Guid Id, string Email);
public sealed class AuthenticationException(string code, int statusCode, IDictionary<string, string[]>? errors = null) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public IDictionary<string, string[]>? Errors { get; } = errors;
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}

public sealed class AuthenticationService(
    OrderFlowDbContext db,
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider)
{
    private const string CustomerRole = "Customer";
    private JwtOptions Jwt => jwtOptions.Value;

    public async Task<RegisteredUser> RegisterAsync(string email, string password, string firstName, string lastName, CancellationToken cancellationToken)
    {
        var normalizedEmail = users.NormalizeEmail(email);
        if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken))
            throw new AuthenticationException("EMAIL_ALREADY_EXISTS", 409);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = email, Email = email, FirstName = firstName.Trim(), LastName = lastName.Trim(),
            IsActive = true, CreatedAtUtc = timeProvider.GetUtcNow(), LockoutEnabled = true
        };
        IdentityResult result;
        try { result = await users.CreateAsync(user, password); }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            db.Entry(user).State = EntityState.Detached;
            throw new AuthenticationException("EMAIL_ALREADY_EXISTS", 409);
        }
        if (!result.Succeeded)
        {
            if (result.Errors.Any(x => x.Code == "DuplicateEmail"))
                throw new AuthenticationException("EMAIL_ALREADY_EXISTS", 409);
            var errors = result.Errors.GroupBy(error =>
                    error.Code.Contains("Email", StringComparison.OrdinalIgnoreCase) ? "email" :
                    error.Code.Contains("Password", StringComparison.OrdinalIgnoreCase) ? "password" : "request")
                .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray());
            throw new AuthenticationException("VALIDATION_FAILED", 400, errors);
        }

        if (!await roles.RoleExistsAsync(CustomerRole))
            throw new InvalidOperationException("Required Customer role is not seeded.");
        var roleResult = await users.AddToRoleAsync(user, CustomerRole);
        if (!roleResult.Succeeded) throw new InvalidOperationException("Could not assign the default Customer role.");
        await transaction.CommitAsync(cancellationToken);
        return new RegisteredUser(user.Id, user.Email!);
    }

    public async Task<AuthTokens> LoginAsync(string email, string password, string? ipAddress, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null || !user.IsActive)
            throw new AuthenticationException("AUTH_INVALID_CREDENTIALS", 401);
        if (!await users.CheckPasswordAsync(user, password))
        {
            if (await users.IsLockedOutAsync(user))
                throw new AuthenticationException("AUTH_INVALID_CREDENTIALS", 401);
            await users.AccessFailedAsync(user);
            if (await users.IsLockedOutAsync(user)) throw new AuthenticationException("ACCOUNT_LOCKED", 423);
            throw new AuthenticationException("AUTH_INVALID_CREDENTIALS", 401);
        }
        if (await users.IsLockedOutAsync(user)) throw new AuthenticationException("ACCOUNT_LOCKED", 423);

        await users.ResetAccessFailedCountAsync(user);
        var raw = NewRefreshToken();
        var now = timeProvider.GetUtcNow();
        db.RefreshTokens.Add(NewRefreshRow(user.Id, Hash(raw), Guid.NewGuid(), now, ipAddress));
        await db.SaveChangesAsync(cancellationToken);
        return new AuthTokens(await CreateAccessTokenAsync(user, cancellationToken), now.AddMinutes(Jwt.AccessTokenMinutes), raw);
    }

    public async Task<AuthTokens> RefreshAsync(string rawToken, string? ipAddress, CancellationToken cancellationToken)
    {
        var hash = Hash(rawToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var current = await db.RefreshTokens.FromSqlInterpolated($"SELECT * FROM RefreshTokens WITH (UPDLOCK, ROWLOCK) WHERE TokenHash = {hash}")
            .SingleOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (current is null) throw new AuthenticationException("REFRESH_TOKEN_REVOKED", 401);
        if (current.RevokedAtUtc is not null)
        {
            await RevokeFamilyAsync(current.FamilyId, now, ipAddress, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new AuthenticationException("REFRESH_TOKEN_REVOKED", 401);
        }
        if (current.ExpiresAtUtc <= now)
        {
            current.RevokedAtUtc = now;
            current.RevokedByIp = ipAddress;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new AuthenticationException("REFRESH_TOKEN_REVOKED", 401);
        }

        var user = await users.FindByIdAsync(current.UserId.ToString());
        if (user is null || !user.IsActive)
        {
            await RevokeFamilyAsync(current.FamilyId, now, ipAddress, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new AuthenticationException("REFRESH_TOKEN_REVOKED", 401);
        }

        var raw = NewRefreshToken();
        var replacement = NewRefreshRow(user.Id, Hash(raw), current.FamilyId, now, ipAddress);
        current.RevokedAtUtc = now;
        current.RevokedByIp = ipAddress;
        current.ReplacedByTokenId = replacement.Id;
        db.RefreshTokens.Add(replacement);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AuthTokens(await CreateAccessTokenAsync(user, cancellationToken), now.AddMinutes(Jwt.AccessTokenMinutes), raw);
    }

    public async Task LogoutAsync(string rawToken, string? ipAddress, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var hash = Hash(rawToken);
        var token = await db.RefreshTokens.FromSqlInterpolated($"SELECT * FROM RefreshTokens WITH (UPDLOCK, ROWLOCK) WHERE TokenHash = {hash}")
            .SingleOrDefaultAsync(cancellationToken);
        if (token is not null && token.RevokedAtUtc is null)
        {
            token.RevokedAtUtc = timeProvider.GetUtcNow();
            token.RevokedByIp = ipAddress;
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, string? ipAddress, CancellationToken cancellationToken)
    {
        await db.RefreshTokens.Where(x => x.FamilyId == familyId && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAtUtc, now).SetProperty(x => x.RevokedByIp, ipAddress), cancellationToken);
    }

    private async Task<string> CreateAccessTokenAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var roles = await users.GetRolesAsync(user);
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new(JwtRegisteredClaimNames.Email, user.Email!), new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Jwt.SigningKey));
        var token = new JwtSecurityToken(Jwt.Issuer, Jwt.Audience, claims, timeProvider.GetUtcNow().UtcDateTime,
            timeProvider.GetUtcNow().AddMinutes(Jwt.AccessTokenMinutes).UtcDateTime, new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        cancellationToken.ThrowIfCancellationRequested();
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private RefreshToken NewRefreshRow(Guid userId, string hash, Guid familyId, DateTimeOffset now, string? ip) => new()
    {
        Id = Guid.NewGuid(), UserId = userId, TokenHash = hash, FamilyId = familyId, CreatedAtUtc = now,
        ExpiresAtUtc = now.AddDays(Jwt.RefreshTokenDays), CreatedByIp = ip
    };

    private static string NewRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
