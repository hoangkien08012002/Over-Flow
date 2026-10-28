using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OrderFlow.Api;
using OrderFlow.Application.Inventory;
using OrderFlow.Infrastructure.Identity;
using OrderFlow.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

string GetConnectionString() => builder.Configuration.GetConnectionString("OrderFlow")
    ?? Environment.GetEnvironmentVariable("ORDERFLOW_SQL_CONNECTION")
    ?? "Server=localhost;Database=OrderFlow;Integrated Security=true;TrustServerCertificate=true";
builder.Services.AddDbContext<OrderFlowDbContext>(options => options.UseSqlServer(GetConnectionString()));
builder.Services.AddOptions<JwtOptions>().Bind(builder.Configuration.GetSection("Jwt"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer) && !string.IsNullOrWhiteSpace(options.Audience) && Encoding.UTF8.GetByteCount(options.SigningKey) >= 32,
        "Jwt:Issuer, Jwt:Audience and a Jwt:SigningKey of at least 32 UTF-8 bytes are required.")
    .Validate(options => options.AccessTokenMinutes is >= 1 and <= 60 && options.RefreshTokenDays is >= 1 and <= 365,
        "JWT token lifetimes are outside the supported range.")
    .ValidateOnStart();
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<OrderFlowDbContext>()
    .AddSignInManager();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AuthenticationService>();
builder.Services.AddOptions<InventoryOptions>().Bind(builder.Configuration.GetSection("Inventory"))
    .Validate(x => x.ReservationMinutes is >= 1 and <= 1440, "Inventory reservation lifetime must be 1 to 1440 minutes.")
    .ValidateOnStart();
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<IInventoryService>(sp => sp.GetRequiredService<InventoryService>());
builder.Services.AddScoped<IReservationExpiryJob>(sp => sp.GetRequiredService<InventoryService>());
builder.Services.AddScoped<IAuthorizationHandler, OwnResourceHandler>();
builder.Services.AddHostedService<RoleSeeder>();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    var status = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
    context.ProblemDetails.Type ??= $"https://orderflow/errors/{status}";
    context.ProblemDetails.Extensions["code"] = status switch
    {
        400 => "VALIDATION_FAILED",
        401 => "AUTHENTICATION_REQUIRED",
        403 => "FORBIDDEN",
        404 => "RESOURCE_NOT_FOUND",
        429 => "RATE_LIMITED",
        _ => "INTERNAL_ERROR"
    };
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.Items[CorrelationIdMiddleware.ItemKey]?.ToString() ?? "";
});
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer,
        ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
        ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "sub", RoleClaimType = ClaimTypes.Role
    };
    options.Events = new JwtBearerEvents
    {
        OnChallenge = async context => { context.HandleResponse(); await ApiErrors.WriteAsync(context.HttpContext, 401, "AUTHENTICATION_REQUIRED", "Authentication is required"); },
        OnForbidden = async context => await ApiErrors.WriteAsync(context.HttpContext, 403, "FORBIDDEN", "Access is forbidden")
    };
});
builder.Services.AddAuthorization(options =>
{
    foreach (var role in new[] { "Customer", "Support", "InventoryManager", "Admin" })
        options.AddPolicy(role, policy => policy.RequireAuthenticatedUser().RequireRole(role));
    options.AddPolicy("OrderRead", policy => policy.RequireAuthenticatedUser().RequireRole("Customer", "Support", "Admin"));
    options.AddPolicy("OwnResource", policy => policy.RequireAuthenticatedUser().AddRequirements(new OwnResourceRequirement()));
});
builder.Services.AddRateLimiter(options =>
{
    foreach (var (name, limit) in new[] { ("register", 20), ("login", 20), ("refresh", 20), ("logout", 20) })
        options.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.RejectionStatusCode = 429;
    options.OnRejected = async (context, _) => await ApiErrors.WriteAsync(context.HttpContext, 429, "RATE_LIMITED", "Too many requests");
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "OrderFlow API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Name = "Authorization", Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header });
});

var app = builder.Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ProblemDetailsExceptionMiddleware>();
app.UseMiddleware<UnhandledStatusProblemMiddleware>();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<ActiveAccountMiddleware>();
app.UseAuthorization();

app.MapPost("/api/v1/auth/register", async (RegisterRequest request, AuthenticationService auth, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 256 || string.IsNullOrWhiteSpace(request.Password) ||
        string.IsNullOrWhiteSpace(request.FirstName) || request.FirstName.Length > 100 || string.IsNullOrWhiteSpace(request.LastName) || request.LastName.Length > 100)
        return Results.Json(new { type = "https://orderflow/errors/validation_failed", title = "Request validation failed", status = 400, code = "VALIDATION_FAILED", correlationId = context.Items[CorrelationIdMiddleware.ItemKey], errors = new { request = new[] { "Email, password, first name and last name are required and must fit their documented lengths." } } }, statusCode: 400, contentType: "application/problem+json");
    var user = await auth.RegisterAsync(request.Email.Trim(), request.Password, request.FirstName, request.LastName, context.RequestAborted);
    return Results.Created("/api/v1/auth/register", user);
}).AllowAnonymous().RequireRateLimiting("register");

app.MapPost("/api/v1/auth/login", async (LoginRequest request, AuthenticationService auth, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password)) throw new AuthenticationException("AUTH_INVALID_CREDENTIALS", 401);
    var tokens = await auth.LoginAsync(request.Email.Trim(), request.Password, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);
    return Results.Ok(tokens);
}).AllowAnonymous().RequireRateLimiting("login");

app.MapPost("/api/v1/auth/refresh", async (RefreshRequest request, AuthenticationService auth, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.RefreshToken)) throw new AuthenticationException("REFRESH_TOKEN_REVOKED", 401);
    return Results.Ok(await auth.RefreshAsync(request.RefreshToken, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted));
}).AllowAnonymous().RequireRateLimiting("refresh");

app.MapPost("/api/v1/auth/logout", async (RefreshRequest request, AuthenticationService auth, HttpContext context) =>
{
    if (!string.IsNullOrWhiteSpace(request.RefreshToken)) await auth.LogoutAsync(request.RefreshToken, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);
    return Results.NoContent();
}).AllowAnonymous().RequireRateLimiting("logout");

app.MapCatalogEndpoints();
app.MapInventoryEndpoints();

app.Run();

public sealed record RegisterRequest(string Email, string Password, string FirstName, string LastName);
public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public partial class Program;
