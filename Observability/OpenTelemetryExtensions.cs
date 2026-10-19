using System.Diagnostics;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AZOA.WebAPI.Observability;

/// <summary>Configures tracing, metrics and W3C request correlation; see Observability/AGENTS.md.</summary>
public static class OpenTelemetryExtensions
{
    /// <summary>
    /// Activity source for custom instrumentation across AZOA managers and controllers.
    /// Use this to create spans for domain-significant operations.
    /// </summary>
    public const string ActivitySourceName = "AZOA.WebAPI";

    /// <summary>Shared source and meter name for the Surreal executor decorator.</summary>
    public const string SurrealInstrumentationName = "SurrealForge";

    /// <summary>
    /// Shared ActivitySource instance for use by controllers and managers.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>Registers instrumentation and optionally OTLP export.</summary>
    public static IServiceCollection AddAzoaObservability(this IServiceCollection services, IConfiguration config)
    {
        var serviceName = config["OpenTelemetry:ServiceName"] ?? "AZOA.WebAPI";
        var exportEnabled = config.GetValue("OpenTelemetry:ExportEnabled", true);

        var otlpEndpoint = config["OpenTelemetry:Otlp:Endpoint"];
        var otlpProtocolRaw = config["OpenTelemetry:Otlp:Protocol"];
        var otlpProtocol = otlpProtocolRaw?.ToLowerInvariant() switch
        {
            "http/protobuf" => OtlpExportProtocol.HttpProtobuf,
            _ => OtlpExportProtocol.Grpc
        };

        services
            .AddOpenTelemetry()
            .ConfigureResource(resource =>
                resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(ActivitySourceName)
                    .AddSource(SurrealInstrumentationName)
                    .AddAspNetCoreInstrumentation(opts =>
                    {
                        // Enrich spans with request correlation attributes
                        opts.EnrichWithHttpRequest = (activity, request) =>
                            activity.SetTag("http.request.id", request.HttpContext.TraceIdentifier);
                    })
                    .AddHttpClientInstrumentation();

                if (!exportEnabled) return;

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(opts =>
                    {
                        opts.Endpoint = new Uri(otlpEndpoint);
                        opts.Protocol = otlpProtocol;
                    });
                }
                else
                {
                    // Preserve SDK endpoint defaults and OTEL_EXPORTER_OTLP_ENDPOINT overrides.
                    tracing.AddOtlpExporter(opts =>
                    {
                        opts.Protocol = otlpProtocol;
                    });
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(SurrealInstrumentationName);

                if (!exportEnabled) return;

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(opts =>
                    {
                        opts.Endpoint = new Uri(otlpEndpoint);
                        opts.Protocol = otlpProtocol;
                    });
                }
                else
                {
                    metrics.AddOtlpExporter(opts =>
                    {
                        opts.Protocol = otlpProtocol;
                    });
                }
            });

        return services;
    }

    /// <summary>
    /// Adds middleware that attaches the W3C TraceId and SpanId as structured log properties
    /// for every request, so all log entries carry the correlation identifiers.
    /// Call this after UseRouting() and before UseEndpoints()/MapControllers().
    /// </summary>
    public static IApplicationBuilder UseAzoaRequestCorrelation(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            var activity = Activity.Current;
            if (activity is not null)
            {
                // Expose trace/span ids as structured log scope so ILogger sinks pick them up
                using var logScope = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("AZOA.RequestCorrelation")
                    .BeginScope(new Dictionary<string, object>
                    {
                        ["TraceId"] = activity.TraceId.ToString(),
                        ["SpanId"] = activity.SpanId.ToString()
                    });

                await next(context);
            }
            else
            {
                await next(context);
            }
        });

        return app;
    }
}
