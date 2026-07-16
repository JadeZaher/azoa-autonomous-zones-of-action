using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using AZOA.WebAPI.Observability;
using FluentAssertions;
using Moq;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Tests.Observability;

public sealed class InstrumentedSurrealExecutorTests
{
    [Fact]
    public async Task Executor_EmitsThroughRegisteredSurrealSourceAndMeter()
    {
        Activity? stoppedActivity = null;
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == OpenTelemetryExtensions.SurrealInstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity => stoppedActivity = activity,
        };
        ActivitySource.AddActivityListener(activityListener);

        var publishedInstruments = new HashSet<string>(StringComparer.Ordinal);
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name != OpenTelemetryExtensions.SurrealInstrumentationName)
                return;

            publishedInstruments.Add(instrument.Name);
            listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        meterListener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        meterListener.Start();

        var inner = new Mock<ISurrealExecutor>();
        inner.Setup(executor => executor.ExecuteAsync(
                It.IsAny<SurrealQuery>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("executor failure"));
        var executor = CreateInstrumentedExecutor(inner.Object);

        Func<Task> act = () => executor.ExecuteAsync(SurrealQuery.Of("RETURN 1"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        stoppedActivity.Should().NotBeNull();
        stoppedActivity!.Source.Name.Should()
            .Be(OpenTelemetryExtensions.SurrealInstrumentationName);
        stoppedActivity.Status.Should().Be(ActivityStatusCode.Error);
        stoppedActivity.StatusDescription.Should().BeNull();
        stoppedActivity.Tags.Should().Contain(tag =>
            tag.Key == "exception.type"
            && tag.Value == typeof(InvalidOperationException).FullName);
        stoppedActivity.Tags.Should().NotContain(tag => tag.Key == "exception.message");
        publishedInstruments.Should().Contain("surrealdb.queries");
        publishedInstruments.Should().Contain("surrealdb.errors");
        publishedInstruments.Should().Contain("surrealdb.duration_ms");
    }

    private static ISurrealExecutor CreateInstrumentedExecutor(ISurrealExecutor inner)
    {
        var type = typeof(OpenTelemetryExtensions).Assembly.GetType(
            "AZOA.WebAPI.Observability.InstrumentedSurrealExecutor",
            throwOnError: true)!;
        return (ISurrealExecutor)Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [inner],
            culture: null)!;
    }
}
