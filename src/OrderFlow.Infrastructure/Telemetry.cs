using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Infrastructure;

public sealed class TelemetryOptions
{
    // Export is off until an OTLP endpoint is configured, so a local run needs no collector. The meters still work with dotnet-counters.
    public string? OtlpEndpoint { get; set; }
    public bool Valid => string.IsNullOrEmpty(OtlpEndpoint) ||
        Uri.TryCreate(OtlpEndpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

public static class TelemetryRegistration
{
    public static IServiceCollection AddOrderFlowTelemetry(this IServiceCollection services, IConfiguration configuration, string serviceName, Action<MeterProviderBuilder>? metrics = null)
    {
        var options = configuration.GetSection("Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();
        if (!options.Valid) throw new InvalidOperationException("Telemetry:OtlpEndpoint must be an absolute http or https URL.");
        if (string.IsNullOrEmpty(options.OtlpEndpoint)) return services;
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithMetrics(builder =>
            {
                builder.AddMeter("OrderFlow.Cache", "OrderFlow.Messaging", BusinessMetrics.MeterName);
                metrics?.Invoke(builder);
                builder.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint));
            });
        return services;
    }
}
