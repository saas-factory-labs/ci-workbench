using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Standard Aspire service defaults (service discovery, resilience, health checks, OpenTelemetry), kept close to the
// official template. Everything here is inert unless configured: the OTLP exporter is only enabled when
// OTEL_EXPORTER_OTLP_ENDPOINT is present (set automatically by the Aspire AppHost), so a standalone
// `dotnet run` of the web app behaves as before and needs no collector, dashboard or container.
// See docs/architecture/observability.md.
public sealed class ServiceDefaultsOptions
{
    /// <summary>
    /// Adds the standard Aspire resilience handler (retry, circuit breaker, timeouts) to every HttpClient. On by default.
    /// Turn it off in an app whose outbound clients carry their own domain-aware retry policy, so retries do not stack.
    /// </summary>
    public bool StandardHttpResilience { get; set; } = true;
}

public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string ReadinessEndpointPath = "/ready";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder, Action<ServiceDefaultsOptions>? configure = null) where TBuilder : IHostApplicationBuilder
    {
        var options = new ServiceDefaultsOptions();
        configure?.Invoke(options);

        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default (opt out via ServiceDefaultsOptions.StandardHttpResilience).
            if (options.StandardHttpResilience) http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                            && !context.Request.Path.StartsWithSegments(ReadinessEndpointPath)
                    )
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Health endpoints are mapped in Development only (the Aspire/local scenario), as in the official template:
        // exposing them elsewhere has security implications (https://aka.ms/dotnet/aspire/healthchecks).
        if (app.Environment.IsDevelopment())
        {
            // Everything registered must be healthy.
            app.MapHealthChecks(HealthEndpointPath);

            // Liveness: only checks tagged "live" (the process is responsive).
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });

            // Readiness: live + "ready" checks (e.g. GitHub configuration / rate-limit state). Never calls external services.
            app.MapHealthChecks(ReadinessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live") || r.Tags.Contains("ready")
            });
        }

        return app;
    }
}