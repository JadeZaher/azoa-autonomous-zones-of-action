using System.Reflection;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AZOA.WebAPI.Tests.NodeAdmin;

/// <summary>Bootstrap cancellation remains safe across host stop and service-provider disposal.</summary>
public sealed class NodeAdminBootstrapLifecycleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stop_and_dispose_are_safe_in_either_order_and_cancel_a_pending_retry(bool disposeFirst)
    {
        var (service, scopes) = CreateService();
        await service.StartAsync(CancellationToken.None);

        if (disposeFirst) service.Dispose();
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose();
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose();

        scopes.Verify(factory => factory.CreateScope(), Times.Once,
            "shutdown must cancel the scheduled retry rather than run another bootstrap attempt");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Overlapping_stop_and_dispose_keep_the_source_live_until_its_cancellation_callbacks_finish(bool stopOwnsSource)
    {
        var (service, _) = CreateService();
        await service.StartAsync(CancellationToken.None);
        var source = StoppingSource(service);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = source.Token.Register(() =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
        });
        var dispose = stopOwnsSource ? Task.CompletedTask : Task.Run(service.Dispose);
        var stop = Task.CompletedTask;
        try
        {
            if (!stopOwnsSource)
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop = service.StopAsync(CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (stopOwnsSource) stop.IsCompleted.Should().BeFalse();
            service.Dispose();
            Action readToken = () => _ = source.Token;
            readToken.Should().NotThrow("the stopping caller owns the source until cancellation completes");
        }
        finally
        {
            release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            service.Dispose();
        }
        Action readDisposedToken = () => _ = source.Token;
        readDisposedToken.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Host_stop_deadline_bounds_waiting_for_cancellation_callbacks()
    {
        var (service, _) = CreateService();
        var source = StoppingSource(service);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = source.Token.Register(() =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
            exited.SetResult();
        });
        using var deadline = new CancellationTokenSource();
        var stop = service.StopAsync(deadline.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await deadline.CancelAsync();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            exited.Task.IsCompleted.Should().BeFalse("the host deadline must not wait for a blocked callback");
            service.Dispose();
        }
        finally
        {
            release.TrySetResult();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            service.Dispose();
        }
    }

    private static (NodeAdminBootstrapService Service, Mock<IServiceScopeFactory> Scopes) CreateService()
    {
        var scopes = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        scopes.Setup(factory => factory.CreateScope()).Throws(new IOException("bootstrap unavailable"));
        var runtime = new NodeAdminRuntime(Options.Create(new NodeAdminOptions()),
            new ConfigurationBuilder().Build(), NullLogger<NodeAdminRuntime>.Instance);
        return (new NodeAdminBootstrapService(scopes.Object, runtime, NullLogger<NodeAdminBootstrapService>.Instance), scopes);
    }

    private static CancellationTokenSource StoppingSource(NodeAdminBootstrapService service)
        => (CancellationTokenSource)typeof(NodeAdminBootstrapService)
            .GetField("_stopping", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
}
