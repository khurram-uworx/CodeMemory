using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CodeMemory.ServiceDefaults;

public static class OpenTelemetryExtensions
{
    public static IServiceCollection AddCodeMemoryOpenTelemetry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: "codememory-aspnet",
                serviceVersion: "0.4.2"))
            .WithLogging(logging => logging
                .AddOtlpExporter())
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddEntityFrameworkCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource("CodeMemory.*")
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("CodeMemory")
                .AddOtlpExporter());

        return services;
    }
}
