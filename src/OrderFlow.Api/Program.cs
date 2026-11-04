using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OpenTelemetry.Metrics;
using OrderFlow.Api;
using OrderFlow.Application.Catalog;
using OrderFlow.Application.Inventory;
using OrderFlow.Infrastructure;
using OrderFlow.Infrastructure.Caching;
using OrderFlow.Infrastructure.Identity;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Structured console output keeps the correlation scope on every line, so one order can be followed across the API and Worker logs.
if (!builder.Environment.IsDevelopment()) builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

// Only Development may fall back to a local database. Elsewhere a missing setting must stop startup rather than point at the wrong server.
string GetConnectionString() => builder.Configuration.GetConnectionString("OrderFlow")
    ?? Environment.GetEnvironmentVariable("ORDERFLOW_SQL_CONNECTION")
    ?? (builder.Environment.IsDevelopment() ? "Server=localhost;Database=OrderFlow;Integrated Security=true;TrustServerCertificate=true"
        : throw new InvalidOperationException("ConnectionStrings:OrderFlow or ORDERFLOW_SQL_CONNECTION is required."));
builder.Services.AddDbContext<OrderFlowDbContext>(options => options.UseSqlServer(GetConnectionString()));
builder.Services.AddOrderFlowTelemetry(builder.Configuration, "OrderFlow.Api", metrics => metrics.AddAspNetCoreInstrumentation());
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
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddScoped<ShippingService>();
builder.Services.AddScoped<ReturnService>();
builder.Services.AddOptions<ReturnOptions>().Bind(builder.Configuration.GetSection("Returns"))
    .Validate(x => x.WindowDays is >= 1 and <= 365, "Returns:WindowDays must be 1 to 365.")
    .ValidateOnStart();
builder.Services.AddOptions<RateLimitOptions>().Bind(builder.Configuration.GetSection("RateLimits"))
    .Validate(x => new[] { x.Register, x.Login, x.Refresh, x.Logout, x.Search, x.Checkout }.All(limit => limit >= 1), "Every rate limit must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<CorsSettings>().Bind(builder.Configuration.GetSection("Cors"))
    .Validate(x => CorsPolicy.Valid(x, builder.Environment.IsDevelopment()), "Cors:AllowedOrigins must be exact origins without a wildcard, and HTTPS outside Development.")
    .ValidateOnStart();
builder.Services.AddOptions<ProxySettings>().Bind(builder.Configuration.GetSection("Proxy"))
    .Validate(ProxyConfiguration.Valid, "Proxy:TrustedProxies must be IP addresses.")
    .ValidateOnStart();
builder.Services.AddOptions<KafkaOptions>().Bind(builder.Configuration.GetSection("Kafka"))
    .Validate(x => !string.IsNullOrWhiteSpace(x.BootstrapServers), "Kafka:BootstrapServers is required.")
    .ValidateOnStart();
builder.Services.AddCors();
builder.Services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>().Configure<Microsoft.Extensions.Options.IOptions<CorsSettings>>((options, settings) =>
    options.AddPolicy(CorsPolicy.Name, policy => policy.WithOrigins(settings.Value.AllowedOrigins)
        .WithMethods("GET", "POST", "PUT", "DELETE")
        .WithHeaders("Authorization", "Content-Type", "Idempotency-Key", "X-Correlation-Id", RefreshCookie.TransportHeader)
        .WithExposedHeaders("X-Correlation-Id", "Location", "Retry-After")
        .AllowCredentials()));
builder.Services.AddSingleton<KafkaHealthCheck>();
builder.Services.AddHealthChecks()
    .AddCheck<SqlHealthCheck>("sql")
    .AddCheck<RedisHealthCheck>("redis")
    .AddCheck<KafkaHealthCheck>("kafka");
builder.Services.AddOptions<OrderingOptions>().Bind(builder.Configuration.GetSection("Ordering"))
    .Validate(x => x.IdempotencyRetentionHours is >= 1 and <= 720, "Idempotency retention must be 1 to 720 hours.")
    .ValidateOnStart();
builder.Services.AddOptions<RetentionOptions>().Bind(builder.Configuration.GetSection("Retention"))
    .Validate(x => x.InboxDays >= 1, "Retention:InboxDays must be positive.")
    .ValidateOnStart();
// Status values are part of the public contract, so they stay readable and do not shift when an enum is reordered.
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOptions<RedisOptions>().Bind(builder.Configuration.GetSection("Redis"))
    // Scoping keys to the authoritative database keeps environments that share one Redis from reading each other's entries.
    .PostConfigure(x => x.KeyPrefix ??= $"{new SqlConnectionStringBuilder(GetConnectionString()).InitialCatalog}:")
    .Validate(x => !string.IsNullOrWhiteSpace(x.ConnectionString), "Redis:ConnectionString is required.")
    .Validate(x => x.TimeoutMilliseconds is >= 10 and <= 5000, "Redis timeout must be 10 to 5000 milliseconds.")
    .ValidateOnStart();
builder.Services.AddSingleton<IReadCache, RedisReadCache>();
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
    options.AddPolicy("OrderStaff", policy => policy.RequireAuthenticatedUser().RequireRole("Support", "Admin"));
    options.AddPolicy("Fulfillment", policy => policy.RequireAuthenticatedUser().RequireRole("InventoryManager", "Admin"));
    options.AddPolicy("ReturnStaff", policy => policy.RequireAuthenticatedUser().RequireRole("Support", "InventoryManager", "Admin"));
    options.AddPolicy("OwnResource", policy => policy.RequireAuthenticatedUser().AddRequirements(new OwnResourceRequirement()));
});
builder.Services.AddRateLimiter(options =>
{
    var limits = builder.Configuration.GetSection("RateLimits").Get<RateLimitOptions>() ?? new RateLimitOptions();
    // Partitioned by client address, because the limiter runs before authentication and must also protect anonymous endpoints.
    foreach (var (name, limit) in new[] { ("register", limits.Register), ("login", limits.Login), ("refresh", limits.Refresh), ("logout", limits.Logout), ("search", limits.Search), ("checkout", limits.Checkout) })
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
    options.OperationFilter<AuthorizationOperationFilter>();
});

var app = builder.Build();
var proxySettings = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProxySettings>>().Value;
if (proxySettings.TrustedProxies.Length > 0)
{
    var forwarded = new ForwardedHeadersOptions();
    ProxyConfiguration.Apply(forwarded, proxySettings);
    app.UseForwardedHeaders(forwarded);
}
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ProblemDetailsExceptionMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<UnhandledStatusProblemMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
else app.UseHsts();
app.UseCors(CorsPolicy.Name);
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
    return TokenResult(context, await auth.LoginAsync(request.Email.Trim(), request.Password, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted));
}).AllowAnonymous().RequireRateLimiting("login");

app.MapPost("/api/v1/auth/refresh", async (RefreshRequest request, AuthenticationService auth, HttpContext context) =>
{
    var token = RefreshTokenFrom(context, request);
    if (string.IsNullOrWhiteSpace(token)) throw new AuthenticationException("REFRESH_TOKEN_REVOKED", 401);
    return TokenResult(context, await auth.RefreshAsync(token, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted));
}).AllowAnonymous().RequireRateLimiting("refresh");

app.MapPost("/api/v1/auth/logout", async (RefreshRequest request, AuthenticationService auth, HttpContext context) =>
{
    var token = RefreshTokenFrom(context, request);
    if (!string.IsNullOrWhiteSpace(token)) await auth.LogoutAsync(token, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);
    if (RefreshCookie.Requested(context)) context.Response.Cookies.Delete(RefreshCookie.Name, RefreshCookie.Options(context, null));
    return Results.NoContent();
}).AllowAnonymous().RequireRateLimiting("logout");

app.MapCatalogEndpoints();
app.MapInventoryEndpoints();
app.MapOrderingEndpoints();
app.MapAdminEndpoints();
app.MapFulfillmentEndpoints();
app.MapEngagementEndpoints();
app.MapHealthEndpoints();

app.Run();

static string? RefreshTokenFrom(HttpContext context, RefreshRequest request) =>
    !string.IsNullOrWhiteSpace(request.RefreshToken) ? request.RefreshToken : RefreshCookie.Requested(context) ? context.Request.Cookies[RefreshCookie.Name] : null;

// A browser client gets the refresh token only as an HttpOnly cookie. Other clients keep the documented JSON response.
static IResult TokenResult(HttpContext context, AuthTokens tokens)
{
    if (!RefreshCookie.Requested(context)) return Results.Ok(tokens);
    var days = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<JwtOptions>>().Value.RefreshTokenDays;
    context.Response.Cookies.Append(RefreshCookie.Name, tokens.RefreshToken, RefreshCookie.Options(context, tokens.AccessTokenExpiresAtUtc.AddDays(days)));
    return Results.Ok(new { tokens.AccessToken, tokens.AccessTokenExpiresAtUtc });
}

public sealed record RegisterRequest(string Email, string Password, string FirstName, string LastName);
public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string? RefreshToken);
public partial class Program;
