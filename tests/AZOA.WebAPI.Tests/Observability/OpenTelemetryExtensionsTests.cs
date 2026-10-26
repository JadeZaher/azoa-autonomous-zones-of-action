using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using AZOA.WebAPI.Observability;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AZOA.WebAPI.Tests.Observability;

public sealed class OpenTelemetryExtensionsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Export_setting_controls_only_remote_export_and_defaults_to_enabled(string? configured, bool exports)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenTelemetry:ExportEnabled"] = configured,
            ["OpenTelemetry:Otlp:Protocol"] = "http/protobuf",
            ["OpenTelemetry:Otlp:Endpoint"] = "http://collector.test/v1/probe",
        }).Build();
        var transport = new ProbeTransport();
        var traces = new LocalTraceExporter();
        var metrics = new LocalMetricExporter();
        var clientsCreated = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzoaObservability(config);
        services.AddOpenTelemetry()
            .WithTracing(builder => builder.AddProcessor(new SimpleActivityExportProcessor(traces)))
            .WithMetrics(builder => builder.AddReader(new PeriodicExportingMetricReader(metrics)));
        services.PostConfigureAll<OtlpExporterOptions>(options =>
        {
            options.HttpClientFactory = () =>
            {
                Interlocked.Increment(ref clientsCreated);
                return new HttpClient(transport, disposeHandler: false);
            };
        });
        using var provider = services.BuildServiceProvider();
        var tracer = provider.GetRequiredService<TracerProvider>();
        var meterProvider = provider.GetRequiredService<MeterProvider>();
        var probe = "export-setting-" + Guid.NewGuid().ToString("N");

        using (var activity = OpenTelemetryExtensions.ActivitySource.StartActivity(probe))
        {
            activity.Should().NotBeNull("the configured activity source remains observed without remote export");
            activity!.TraceId.Should().NotBe(default(ActivityTraceId));
        }
        using var meter = new Meter(OpenTelemetryExtensions.SurrealInstrumentationName);
        meter.CreateCounter<long>(probe).Add(1);

        tracer.ForceFlush(1000).Should().BeTrue();
        meterProvider.ForceFlush(1000).Should().BeTrue();
        traces.Names.Should().Contain(probe);
        metrics.Names.Should().Contain(probe, "the configured meter remains observed without remote export");
        if (exports)
        {
            clientsCreated.Should().BeGreaterOrEqualTo(2, "trace and metric OTLP exporters each use the in-process transport");
            transport.Requests.Should().BeGreaterOrEqualTo(2);
        }
        else
        {
            clientsCreated.Should().Be(0);
            transport.Requests.Should().Be(0, "local collection must not create an external collector dependency");
        }
    }

    private sealed class LocalTraceExporter : BaseExporter<Activity>
    {
        public ConcurrentBag<string> Names { get; } = [];

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) Names.Add(activity.OperationName);
            return ExportResult.Success;
        }
    }

    private sealed class LocalMetricExporter : BaseExporter<Metric>
    {
        public ConcurrentBag<string> Names { get; } = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch) Names.Add(metric.Name);
            return ExportResult.Success;
        }
    }

    private sealed class ProbeTransport : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => Respond();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Respond());

        private HttpResponseMessage Respond()
        {
            Interlocked.Increment(ref _requests);
            var content = new ByteArrayContent([]);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
