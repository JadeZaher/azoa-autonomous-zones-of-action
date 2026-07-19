using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Services.Reconciliation;

namespace AZOA.WebAPI.Tests.Services.Reconciliation;

[CollectionDefinition("Reconciliation hosted service", DisableParallelization = true)]
public sealed class ReconciliationHostedServiceCollection { }

/// <summary>
/// <see cref="ReconciliationHostedService"/> safety tests (api-safety-hardening
/// plan tasks 15/16 — the background sweep half).
///
/// <para>The hosted service is a singleton resolving a SCOPED
/// <see cref="IReconciliationService"/> per tick via
/// <see cref="IServiceScopeFactory"/>. These tests prove it:
/// <list type="bullet">
/// <item>runs the scoped service on its interval through a real DI scope,</item>
/// <item>SWALLOWS an exception thrown by the scoped service without crashing
/// the host (the next tick still runs),</item>
/// <item>honours the stopping token — stops promptly, no hang.</item>
/// </list>
/// Determinism: the interval is clamped by the service to a 10s floor, so we
/// NEVER wait a real interval. Instead the mock scoped service signals when a
/// sweep has run and we cancel the host explicitly — the host exits via the
/// <c>OperationCanceledException</c> path on its interval delay long before 10s
/// elapses. No real long sleeps anywhere.</para>
/// </summary>
[Collection("Reconciliation hosted service")]
public class ReconciliationHostedServiceTests
{
    /// <summary>
    /// Host runs the scoped service through a real DI scope, swallows a
    /// first-tick exception WITHOUT tearing down, lets the next tick succeed,
    /// then stops promptly on cancellation. Invariant proven: a flaky sweep
    /// (DB down / RPC flapping) never crashes the host; cancellation is honored.
    /// </summary>
    [Fact]
    public async Task Host_SwallowsScopedException_KeepsRunning_StopsOnCancellation()
    {
        using var cts = new CancellationTokenSource();

        int bridgeCalls = 0;
        var secondSweepReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var svcMock = new Mock<IReconciliationService>();
        svcMock
            .Setup(s => s.ReconcileBridgeAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken _) =>
            {
                var n = Interlocked.Increment(ref bridgeCalls);
                if (n == 1)
                    // First tick throws — must be swallowed, host must survive.
                    throw new InvalidOperationException("simulated sweep failure (DB down)");

                // Second tick proves the host kept running after the failure.
                secondSweepReached.TrySetResult();
                return Task.FromResult(ReconciliationReport.Empty);
            });
        svcMock
            .Setup(s => s.ReconcileOperationsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReconciliationReport.Empty);

        using var scopeFactory = new FakeScopeFactory(svcMock.Object);

        // Interval is clamped to a 10s floor by the service; we never wait it —
        // we cancel as soon as the second (successful) sweep is observed.
        var options = Options.Create(new ReconciliationOptions
        {
            Enabled = true,
            IntervalSeconds = 0,        // clamped to 10s; irrelevant — we cancel first
            StartupDelaySeconds = 0,    // no warm-up delay in the test
        });

        var host = new ReconciliationHostedService(
            scopeFactory,
            Mock.Of<ILogger<ReconciliationHostedService>>(),
            options,
            minimumInterval: TimeSpan.FromMilliseconds(1));

        await host.StartAsync(cts.Token);

        // Wait until the SECOND sweep ran — i.e. the host survived the first
        // throwing tick AND looped again. Bounded so a regression fails fast
        // instead of hanging the suite.
        var reached = await Task.WhenAny(
            secondSweepReached.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        reached.Should().Be(secondSweepReached.Task,
            "the host must swallow the first-tick exception and run a second sweep");

        // Honor the stopping token — StopAsync must return promptly.
        var stop = host.StopAsync(CancellationToken.None);
        var stopped = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10)));
        stopped.Should().Be(stop, "the host must stop promptly when cancelled");
        await stop;

        bridgeCalls.Should().BeGreaterThanOrEqualTo(2,
            "tick 1 threw (swallowed) and tick 2 succeeded — proving resilience");
        scopeFactory.ScopesCreated.Should().BeGreaterThanOrEqualTo(2,
            "each tick must resolve the scoped service in its OWN DI scope");
        scopeFactory.AllScopesDisposed.Should().BeTrue(
            "every per-tick scope must be disposed (no scope leak)");
    }

    /// <summary>
    /// When <c>Reconciliation:Enabled=false</c> the sweep is inert: no scope is
    /// ever created and the scoped service is never invoked (the manual
    /// <c>IReconciliationService</c> path is still usable, but that is not the
    /// host's concern). Invariant proven: the kill switch fully disables the
    /// background sweep.
    /// </summary>
    [Fact]
    public async Task Host_Disabled_NeverCreatesScope_NeverReconciles()
    {
        using var cts = new CancellationTokenSource();

        var svcMock = new Mock<IReconciliationService>();
        using var scopeFactory = new FakeScopeFactory(svcMock.Object);

        var host = new ReconciliationHostedService(
            scopeFactory,
            Mock.Of<ILogger<ReconciliationHostedService>>(),
            Options.Create(new ReconciliationOptions { Enabled = false }));

        await host.StartAsync(cts.Token);
        // ExecuteAsync returns immediately when disabled; give it a brief,
        // bounded yield window (no fixed long sleep — just scheduler turns).
        for (int i = 0; i < 50 && scopeFactory.ScopesCreated == 0; i++)
            await Task.Yield();
        await host.StopAsync(CancellationToken.None);

        scopeFactory.ScopesCreated.Should().Be(0, "a disabled sweep must never create a DI scope");
        svcMock.Verify(s => s.ReconcileBridgeAsync(It.IsAny<CancellationToken>()), Times.Never);
        svcMock.Verify(s => s.ReconcileOperationsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Fake DI scope factory (private, in-file — disjoint ownership).
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Minimal <see cref="IServiceScopeFactory"/> that hands out scopes whose
    /// <see cref="IServiceProvider"/> resolves the supplied scoped
    /// <see cref="IReconciliationService"/>. Tracks scope create/dispose counts
    /// so the test can prove per-tick scoping + no scope leak — without a real
    /// container.
    /// </summary>
    private sealed class FakeScopeFactory : IServiceScopeFactory, IDisposable
    {
        private readonly IReconciliationService _scopedService;
        private readonly List<FakeScope> _scopes = new();
        private readonly object _gate = new();

        public FakeScopeFactory(IReconciliationService scopedService)
            => _scopedService = scopedService;

        public int ScopesCreated
        {
            get { lock (_gate) return _scopes.Count; }
        }

        public bool AllScopesDisposed
        {
            get { lock (_gate) return _scopes.Count > 0 && _scopes.TrueForAll(s => s.Disposed); }
        }

        public IServiceScope CreateScope()
        {
            var scope = new FakeScope(new FakeProvider(_scopedService));
            lock (_gate) _scopes.Add(scope);
            return scope;
        }

        public void Dispose()
        {
            lock (_gate)
                foreach (var s in _scopes) s.Dispose();
        }

        private sealed class FakeScope : IServiceScope
        {
            public FakeScope(IServiceProvider sp) => ServiceProvider = sp;
            public IServiceProvider ServiceProvider { get; }
            public bool Disposed { get; private set; }
            public void Dispose() => Disposed = true;
        }

        private sealed class FakeProvider : IServiceProvider
        {
            private readonly IReconciliationService _svc;
            public FakeProvider(IReconciliationService svc) => _svc = svc;

            public object? GetService(Type serviceType)
                => serviceType == typeof(IReconciliationService) ? _svc : null;
        }
    }
}
