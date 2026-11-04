using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Catalog;
using OrderFlow.Infrastructure.Caching;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Api;

public sealed class RateLimitOptions
{
    public int Register { get; set; } = 20;
    public int Login { get; set; } = 20;
    public int Refresh { get; set; } = 20;
    public int Logout { get; set; } = 20;
    public int Search { get; set; } = 300;
    public int Checkout { get; set; } = 60;
}

public sealed class CorsSettings
{
    public string[] AllowedOrigins { get; set; } = [];
}

public sealed class ProxySettings
{
    public string[] TrustedProxies { get; set; } = [];
}

public static class CorsPolicy
{
    public const string Name = "OrderFlowClients";

    // A wildcard cannot be combined with the refresh cookie, and outside Development an origin that is not HTTPS would expose it.
    public static bool Valid(CorsSettings settings, bool isDevelopment) => settings.AllowedOrigins.All(origin =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.PathAndQuery == "/" && !origin.EndsWith('/') &&
        (uri.Scheme == Uri.UriSchemeHttps || isDevelopment && uri.Scheme == Uri.UriSchemeHttp));
}

public static class ProxyConfiguration
{
    public static bool Valid(ProxySettings settings) => settings.TrustedProxies.All(x => IPAddress.TryParse(x, out _));

    // Forwarded headers are honored only from named proxies. Trusting them from anywhere would let a client choose its own rate-limit partition.
    public static void Apply(ForwardedHeadersOptions options, ProxySettings settings)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var proxy in settings.TrustedProxies) options.KnownProxies.Add(IPAddress.Parse(proxy));
    }
}

// Logs what happened to a request, never its body or query string, since either can hold personal data or tokens.
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try { await next(context); }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            var level = route.StartsWith("/health", StringComparison.Ordinal) ? LogLevel.Debug : context.Response.StatusCode >= 500 ? LogLevel.Error : LogLevel.Information;
            logger.Log(level, "HTTP {Method} {Route} responded {StatusCode} in {ElapsedMs:F1} ms for user {UserId}",
                context.Request.Method, route, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds, context.User.FindFirst("sub")?.Value ?? "anonymous");
        }
    }
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            // This API serves JSON. Only Swagger UI, which exists only in Development, needs to load anything.
            if (!(environment.IsDevelopment() && context.Request.Path.StartsWithSegments("/swagger")))
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            return Task.CompletedTask;
        });
        return next(context);
    }
}

public sealed class SqlHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            return await scope.ServiceProvider.GetRequiredService<OrderFlowDbContext>().Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("SQL Server is unreachable.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { return HealthCheckResult.Unhealthy($"SQL Server is unreachable ({exception.GetType().Name})."); }
    }
}

// Reads fall back to SQL Server without Redis, so its loss is reported as Degraded and never takes an instance out of rotation.
public sealed class RedisHealthCheck(IReadCache cache) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (cache is not RedisReadCache redis) return HealthCheckResult.Healthy("Redis is not in use.");
        return await redis.PingAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded("Redis is unreachable; catalog reads use SQL Server.");
    }
}

// The API never talks to Kafka: checkout stores events in the outbox, so a broker outage only delays them.
public sealed class KafkaHealthCheck(IOptions<KafkaOptions> options) : IHealthCheck, IDisposable
{
    private readonly Lazy<IAdminClient> admin = new(() => new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.Value.BootstrapServers, BrokerAddressFamily = options.Value.AddressFamily })
        .SetLogHandler((_, _) => { }).SetErrorHandler((_, _) => { }).Build());

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = await Task.Run(() => admin.Value.GetMetadata(TimeSpan.FromSeconds(2)), cancellationToken);
            return metadata.Brokers.Count > 0 ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded("Kafka has no brokers; events wait in the outbox.");
        }
        catch (KafkaException) { return HealthCheckResult.Degraded("Kafka is unreachable; events wait in the outbox."); }
    }

    public void Dispose() { if (admin.IsValueCreated) admin.Value.Dispose(); }
}

public static class HealthEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapHealthEndpoints(this WebApplication app)
    {
        // No dependency is checked here: restarting a process cannot fix an unreachable database.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { ResponseWriter = WriteAsync }).AllowAnonymous();
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        // Descriptions are written by these checks. Exception text is left out because it can quote connection details.
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(x => x.Key, x => new { status = x.Value.Status.ToString(), description = x.Value.Description })
        }, Json), context.RequestAborted);
    }
}

public static class RefreshCookie
{
    public const string Name = "orderflow_refresh";
    public const string TransportHeader = "X-Refresh-Token-Transport";

    // A browser client asks for the cookie, so script never holds the refresh token. Requiring this custom header also forces a CORS preflight.
    public static bool Requested(HttpContext context) => string.Equals(context.Request.Headers[TransportHeader], "cookie", StringComparison.OrdinalIgnoreCase);

    public static CookieOptions Options(HttpContext context, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        // Development serves plain HTTP. Everywhere else TLS is required, including behind a proxy.
        Secure = !context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment(),
        Path = "/api/v1/auth",
        Expires = expires
    };
}
