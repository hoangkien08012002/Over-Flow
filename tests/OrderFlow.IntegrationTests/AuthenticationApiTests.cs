using System.IdentityModel.Tokens.Jwt;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using OrderFlow.Infrastructure.Identity;
using OrderFlow.Infrastructure.Persistence;
using OrderFlow.Api;
using Xunit;

namespace OrderFlow.IntegrationTests;

public sealed class AuthenticationApiTests
{
    private const string TestKey = "TEST_ONLY_orderflow_integration_signing_key_32_bytes_min";

    [Fact]
    public async Task Authentication_rotation_lockout_roles_active_status_and_correlation_work()
    {
        var sqlPassword = ReadSqlPassword();
        if (string.IsNullOrWhiteSpace(sqlPassword)) { Assert.Skip("Set MSSQL_SA_PASSWORD or create .env to run SQL Server integration tests."); return; }
        var connection = NewConnection(sqlPassword);
        await using var database = NewContext(connection);
        var migrated = false;
        try
        {
            await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
            migrated = true;
            await using var factory = new AuthFactory(connection.ConnectionString);
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.html", TestContext.Current.CancellationToken)).StatusCode);
            var swagger = await client.GetStringAsync("/swagger/v1/swagger.json", TestContext.Current.CancellationToken);
            Assert.Contains("Bearer", swagger);

            var invalid = await Post(client, "/api/v1/auth/register", new { email = "not-an-email", password = "weak", firstName = "Test", lastName = "User" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            AssertProblem(invalid);
            Assert.Contains("VALIDATION_FAILED", await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            using var malformedJson = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
            {
                Content = new StringContent("{", Encoding.UTF8, "application/json")
            };
            malformedJson.Headers.Add("X-Correlation-Id", "malformed-json");
            using var malformedResponse = await client.SendAsync(malformedJson, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
            AssertProblem(malformedResponse);
            Assert.Equal("malformed-json", malformedResponse.Headers.GetValues("X-Correlation-Id").Single());
            Assert.Contains("VALIDATION_FAILED", await malformedResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var registration = await Register(client, "customer-auth@example.com", true);
            Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
            Assert.Equal("correlation-register", registration.Headers.GetValues("X-Correlation-Id").Single());
            var duplicate = await Register(client, "CUSTOMER-AUTH@example.com");
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            Assert.Contains("EMAIL_ALREADY_EXISTS", await duplicate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Register(client, "race@example.com")));
            Assert.Single(concurrent, x => x.StatusCode == HttpStatusCode.Created);
            Assert.Equal(5, concurrent.Count(x => x.StatusCode == HttpStatusCode.Conflict));

            var tokens = await Login(client, "customer-auth@example.com");
            var sensitiveValues = new List<string> { tokens.RefreshToken, tokens.AccessToken, TestKey };

            var unknown = await Post(client, "/api/v1/auth/login", new { email = "missing@example.com", password = "WrongPassword123!" });
            var wrong = await Post(client, "/api/v1/auth/login", new { email = "customer-auth@example.com", password = "WrongPassword123!" });
            Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
            using var unknownBody = System.Text.Json.JsonDocument.Parse(await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            using var wrongBody = System.Text.Json.JsonDocument.Parse(await wrong.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(unknownBody.RootElement.GetProperty("code").GetString(), wrongBody.RootElement.GetProperty("code").GetString());
            Assert.Equal(unknownBody.RootElement.GetProperty("title").GetString(), wrongBody.RootElement.GetProperty("title").GetString());
            Assert.Contains("AUTH_INVALID_CREDENTIALS", await wrong.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, "/api/v1/auth/login", new { email = "customer-auth@example.com", password = "WrongPassword123!" })).StatusCode);
            Assert.Equal((HttpStatusCode)423, (await Post(client, "/api/v1/auth/login", new { email = "customer-auth@example.com", password = "WrongPassword123!" })).StatusCode);

            Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
            Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
            var registeredUser = await database.Users.AsNoTracking().SingleAsync(x => x.Email == "customer-auth@example.com", TestContext.Current.CancellationToken);
            var stored = await database.RefreshTokens.AsNoTracking().SingleAsync(x => x.UserId == registeredUser.Id, TestContext.Current.CancellationToken);
            Assert.NotEqual(tokens.RefreshToken, stored.TokenHash);
            Assert.Equal(64, stored.TokenHash.Length);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(tokens.RefreshToken))), stored.TokenHash);

            AssertProblem(unknown);
            Assert.Equal("correlation-register", registration.Headers.GetValues("X-Correlation-Id").Single());

            var refreshed = await Post(client, "/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken });
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            var replacement = await refreshed.Content.ReadFromJsonAsync<TokenResponse>(TestContext.Current.CancellationToken);
            Assert.NotNull(replacement);
            Assert.NotEqual(tokens.RefreshToken, replacement.RefreshToken);
            sensitiveValues.Add(replacement.RefreshToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, "/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken })).StatusCode);
            Assert.All(await database.RefreshTokens.AsNoTracking().Where(x => x.FamilyId == stored.FamilyId).ToListAsync(TestContext.Current.CancellationToken), x => Assert.NotNull(x.RevokedAtUtc));

            var raceTokens = await Login(client, "race@example.com");
            sensitiveValues.Add(raceTokens.RefreshToken);
            var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Post(client, "/api/v1/auth/refresh", new { refreshToken = raceTokens.RefreshToken })));
            Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Unauthorized);

            var logoutTokens = await Login(client, "race@example.com");
            sensitiveValues.Add(logoutTokens.RefreshToken);
            Assert.Equal(HttpStatusCode.NoContent, (await Post(client, "/api/v1/auth/logout", new { refreshToken = logoutTokens.RefreshToken })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, "/api/v1/auth/refresh", new { refreshToken = logoutTokens.RefreshToken })).StatusCode);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                foreach (var email in new[] { "support@example.com", "inventory@example.com", "admin@example.com" })
                    Assert.Equal(HttpStatusCode.Created, (await Register(client, email)).StatusCode);
                var support = await manager.FindByEmailAsync("support@example.com");
                var inventory = await manager.FindByEmailAsync("inventory@example.com");
                var admin = await manager.FindByEmailAsync("admin@example.com");
                await manager.AddToRoleAsync(support!, "Support");
                await manager.AddToRoleAsync(inventory!, "InventoryManager");
                await manager.AddToRoleAsync(admin!, "Admin");
            }
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var authorizer = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
                Assert.True((await authorizer.AuthorizeAsync(RolePrincipal("Customer"), "Customer")).Succeeded);
                Assert.False((await authorizer.AuthorizeAsync(RolePrincipal("Customer"), "Admin")).Succeeded);
                Assert.False((await authorizer.AuthorizeAsync(RolePrincipal("Customer"), "InventoryManager")).Succeeded);
                Assert.True((await authorizer.AuthorizeAsync(RolePrincipal("Support"), "Support")).Succeeded);
                Assert.True((await authorizer.AuthorizeAsync(RolePrincipal("InventoryManager"), "InventoryManager")).Succeeded);
                Assert.True((await authorizer.AuthorizeAsync(RolePrincipal("Admin"), "Admin")).Succeeded);
                var jwtOptions = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
                var tokenHandler = new JwtSecurityTokenHandler();
                Assert.True(tokenHandler.ValidateToken(tokens.AccessToken, jwtOptions.TokenValidationParameters, out _).Identity?.IsAuthenticated);
                Assert.Throws<SecurityTokenMalformedException>(() => tokenHandler.ValidateToken("malformed", jwtOptions.TokenValidationParameters, out _));
                Assert.Throws<SecurityTokenExpiredException>(() => tokenHandler.ValidateToken(MakeExpiredToken(), jwtOptions.TokenValidationParameters, out _));

                var challengeHttp = new DefaultHttpContext();
                challengeHttp.Items[CorrelationIdMiddleware.ItemKey] = "missing-token-check";
                await using var challengeBody = new MemoryStream();
                challengeHttp.Response.Body = challengeBody;
                var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, JwtBearerDefaults.AuthenticationScheme, typeof(JwtBearerHandler));
                var challenge = new JwtBearerChallengeContext(challengeHttp, scheme, jwtOptions, new AuthenticationProperties());
                await jwtOptions.Events.OnChallenge(challenge);
                Assert.Equal(StatusCodes.Status401Unauthorized, challengeHttp.Response.StatusCode);
                Assert.Equal("application/problem+json", challengeHttp.Response.ContentType);

                var forbiddenHttp = new DefaultHttpContext();
                forbiddenHttp.Items[CorrelationIdMiddleware.ItemKey] = "forbidden-check";
                await using var forbiddenBody = new MemoryStream();
                forbiddenHttp.Response.Body = forbiddenBody;
                await jwtOptions.Events.OnForbidden(new ForbiddenContext(forbiddenHttp, scheme, jwtOptions));
                Assert.Equal(StatusCodes.Status403Forbidden, forbiddenHttp.Response.StatusCode);
                Assert.Equal("application/problem+json", forbiddenHttp.Response.ContentType);

                var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var admin = await manager.FindByEmailAsync("admin@example.com");
                admin!.IsActive = false;
                await manager.UpdateAsync(admin);

                var disabledContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", admin.Id.ToString())], "Bearer")) };
                disabledContext.Items[CorrelationIdMiddleware.ItemKey] = "disabled-account";
                var accountMiddleware = new ActiveAccountMiddleware(_ => Task.CompletedTask);
                await accountMiddleware.InvokeAsync(disabledContext, scope.ServiceProvider.GetRequiredService<OrderFlowDbContext>());
                Assert.Equal(StatusCodes.Status401Unauthorized, disabledContext.Response.StatusCode);
            }

            var ownerId = Guid.NewGuid();
            var owner = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", ownerId.ToString())], "test"));
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var authorizer = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
                Assert.True((await authorizer.AuthorizeAsync(owner, ownerId, "OwnResource")).Succeeded);
                Assert.False((await authorizer.AuthorizeAsync(owner, Guid.NewGuid(), "OwnResource")).Succeeded);
                var hiddenContext = new DefaultHttpContext { User = owner };
                hiddenContext.Items[CorrelationIdMiddleware.ItemKey] = "ownership-check";
                await using var responseBody = new MemoryStream();
                hiddenContext.Response.Body = responseBody;
                Assert.False(await hiddenContext.RequireOwnerOrHideAsync(authorizer, Guid.NewGuid(), "ORDER_NOT_FOUND"));
                Assert.Equal(StatusCodes.Status404NotFound, hiddenContext.Response.StatusCode);
                responseBody.Position = 0;
                using var problem = await System.Text.Json.JsonDocument.ParseAsync(responseBody, cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal("ORDER_NOT_FOUND", problem.RootElement.GetProperty("code").GetString());
            }

            var persisted = await database.RefreshTokens.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(persisted, row => sensitiveValues.Contains(row.TokenHash, StringComparer.Ordinal));
            Assert.DoesNotContain(factory.LogMessages, line => sensitiveValues.Any(secret => line.Contains(secret, StringComparison.Ordinal)));
        }
        finally
        {
            if (migrated) await database.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Login_rate_limit_uses_problem_details_shape()
    {
        var password = ReadSqlPassword();
        if (string.IsNullOrWhiteSpace(password)) { Assert.Skip("Set MSSQL_SA_PASSWORD or create .env to run SQL Server integration tests."); return; }
        var connection = NewConnection(password);
        await using var db = NewContext(connection);
        var migrated = false;
        try
        {
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken); migrated = true;
            await using var factory = new AuthFactory(connection.ConnectionString);
            using var client = factory.CreateClient();
            HttpResponseMessage? response = null;
            for (var i = 0; i < 21; i++) response = await Post(client, "/api/v1/auth/login", new { email = "missing@example.com", password = "WrongPassword123!" });
            Assert.NotNull(response);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            AssertProblem(response);
            Assert.Contains("RATE_LIMITED", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally { if (migrated) await db.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken); }
    }

    private static async Task<HttpResponseMessage> Register(HttpClient client, string email, bool correlation = false) => await Post(client, "/api/v1/auth/register", new { email, password = "StrongPassword123!", firstName = "Test", lastName = "User" }, correlation);
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, object body, bool correlation = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (correlation) request.Headers.Add("X-Correlation-Id", "correlation-register");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
    private static async Task<TokenResponse> Login(HttpClient client, string email)
    {
        var response = await Post(client, "/api/v1/auth/login", new { email, password = "StrongPassword123!" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(TestContext.Current.CancellationToken))!;
    }
    private static ClaimsPrincipal RolePrincipal(string role) => new(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role)], "test"));
    private static void AssertProblem(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
    }
    private static string MakeExpiredToken()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestKey));
        var token = new JwtSecurityToken("orderflow-tests", "orderflow-api", [new Claim("sub", Guid.NewGuid().ToString())], DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-5), new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
    private static SqlConnectionStringBuilder NewConnection(string password) => new()
    {
        DataSource = $"127.0.0.1,{Environment.GetEnvironmentVariable("ORDERFLOW_SQL_PORT") ?? "1433"}", InitialCatalog = $"OrderFlow_Phase3_{Guid.NewGuid():N}", UserID = "sa", Password = password, TrustServerCertificate = true
    };
    private static OrderFlowDbContext NewContext(SqlConnectionStringBuilder connection) => new(new DbContextOptionsBuilder<OrderFlowDbContext>().UseSqlServer(connection.ConnectionString).Options);
    private static string? ReadSqlPassword()
    {
        var envPassword = Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD");
        if (!string.IsNullOrWhiteSpace(envPassword)) return envPassword;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var envFile = Path.Combine(directory.FullName, ".env");
            if (File.Exists(envFile)) return File.ReadLines(envFile).FirstOrDefault(x => x.StartsWith("MSSQL_SA_PASSWORD=", StringComparison.Ordinal))?.Split('=', 2)[1];
        }
        return null;
    }

    private sealed record TokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc, string RefreshToken);

    private sealed class AuthFactory(string connection) : WebApplicationFactory<Program>
    {
        private readonly CapturingLoggerProvider loggerProvider = new();
        public IReadOnlyCollection<string> LogMessages => loggerProvider.Messages.ToArray();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OrderFlow"] = connection,
                ["Jwt:Issuer"] = "orderflow-tests",
                ["Jwt:Audience"] = "orderflow-api",
                ["Jwt:SigningKey"] = TestKey,
                ["Jwt:AccessTokenMinutes"] = "15",
                ["Jwt:RefreshTokenDays"] = "30"
            }));
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> messages = new();
        public IReadOnlyCollection<string> Messages => messages.ToArray();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
    }
}
