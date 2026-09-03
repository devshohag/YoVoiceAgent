using HealthChecks.RabbitMQ;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CCaaS.Infrastructure.Observability;

/// <summary>
/// Section 17 - Observability & Operations:
/// "OpenTelemetry traces connect API -> database -> RabbitMQ -> worker -> provider operations.
///  Prometheus metrics expose API latency/errors, queue depth, worker failures, active calls,
///  channel delivery failures, Asterisk health and storage usage.
///  Health endpoints: /health/live and /health/ready with checks for SQL, Redis, RabbitMQ,
///  object storage and telephony connectivity."
/// </summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddCcaasObservability(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("ccaas-api"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource("CCaaS")
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddPrometheusExporter());

        // NOTE: could not restore/build-verify these health check packages in the sandbox that
        // produced this template (NuGet was network-blocked there). AspNetCore.HealthChecks.*
        // packages occasionally change method signatures between major versions (e.g. newer
        // AddRabbitMQ releases want a `Func<IServiceProvider, IConnection>` instead of a plain
        // connection string) - if `dotnet build` flags a signature mismatch here after you
        // restore on your own machine, check that package's current README and adjust the
        // call accordingly; the overall wiring/intent stays the same.
        services.AddHealthChecks()
            .AddSqlServer(configuration.GetConnectionString("Default")!, name: "sqlserver", tags: new[] { "ready" })
            .AddRedis(configuration["Redis:ConnectionString"] ?? "localhost:6379", name: "redis", tags: new[] { "ready" })
            .AddRabbitMQ(rabbitConnectionString: BuildRabbitMqUri(configuration), name: "rabbitmq", tags: new[] { "ready" });

        return services;
    }

    private static string BuildRabbitMqUri(IConfiguration configuration)
    {
        var host = configuration["RabbitMq:Host"] ?? "localhost";
        var port = configuration["RabbitMq:Port"] ?? "5672";
        var user = configuration["RabbitMq:Username"] ?? "guest";
        var pass = configuration["RabbitMq:Password"] ?? "guest";
        return $"amqp://{user}:{pass}@{host}:{port}";
    }
}
