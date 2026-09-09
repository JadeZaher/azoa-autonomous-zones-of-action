// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>
/// Releases holds whose window has elapsed, and finishes holds a crash left mid-release.
/// <para>
/// Without it a hold window would be a promise nobody keeps: the beneficiary would have
/// to come back and ask for their own units on the right day, and a node restart between
/// the reserve draw and the outbound move would strand them in <c>Releasing</c> forever.
/// </para>
/// <para><b>Lifetime and scoping.</b> A <see cref="BackgroundService"/> is a singleton, so
/// every cycle opens its own scope and resolves the scoped hold service inside it — a
/// captured scoped dependency would be disposed after the first cycle.</para>
/// <para><b>Consent (OI-25).</b> The sweep is not a consent bypass and does not want to be.
/// It releases under the hold row's own tenant, so the beneficiary's leg is checked against
/// the grant that person made to that tenant — the same check the tenant-driven release
/// path runs. It therefore inherits whatever scope the hold seam declares, and is fixed by
/// fixing that scope rather than by giving the worker a private exemption.</para>
/// </summary>
public sealed class HoldSweepWorker : BackgroundService
{
    /// <summary>Delay before the first cycle so boot traffic is never contended with.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HoldOptions _options;
    private readonly ReserveOptions _reserveOptions;
    private readonly ILogger<HoldSweepWorker> _logger;

    public HoldSweepWorker(
        IServiceScopeFactory scopeFactory,
        HoldOptions options,
        ReserveOptions reserveOptions,
        ILogger<HoldSweepWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _reserveOptions = reserveOptions ?? throw new ArgumentNullException(nameof(reserveOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The chains this sweep is scoped to: what the operator declared for holds, else what
    /// they declared for the reserve. Never a guessed default — releasing value on a chain
    /// nobody named is exactly the kind of quiet reach this node refuses everywhere else.
    /// </summary>
    public IReadOnlyList<string> ResolveSweepChains()
    {
        var declared = _options.SweepChains.Count > 0 ? _options.SweepChains : _reserveOptions.Chains;
        return declared
            .Where(chain => !string.IsNullOrWhiteSpace(chain))
            .Select(chain => chain.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.SweepEnabled)
        {
            _logger.LogInformation(
                "The hold sweep is disabled; set {Section}:SweepEnabled to release due holds automatically.",
                HoldOptions.SectionName);
            return;
        }

        var chains = ResolveSweepChains();
        if (chains.Count == 0)
        {
            _logger.LogWarning(
                "The hold sweep has no chains to sweep: neither {Section}:SweepChains nor {Reserve}:Chains " +
                "names one, so due holds will stay held until something releases them explicitly.",
                HoldOptions.SectionName, ReserveOptions.SectionName);
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(
            _options.SweepIntervalSeconds,
            HoldOptions.MinimumSweepIntervalSeconds,
            HoldOptions.MaximumSweepIntervalSeconds));
        var batchSize = Math.Clamp(_options.SweepBatchSize, 1, HoldOptions.MaximumSweepBatchSize);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var chain in chains)
                    await RunCycleAsync(chain, batchSize, stoppingToken);

                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// One chain, one batch. Every refusal is logged and skipped rather than aborting the
    /// batch: one beneficiary without an account must not stop everyone else being paid.
    /// </summary>
    private async Task RunCycleAsync(string chain, int batchSize, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var holds = scope.ServiceProvider.GetRequiredService<IPlatformHold>();

            var due = await holds.ListDueAsync(chain, DateTimeOffset.UtcNow, batchSize, ct);
            if (due.IsError || due.Result is null)
            {
                _logger.LogWarning(
                    "The hold sweep could not list due holds on {Chain}: {Detail}", chain, due.Message);
                return;
            }

            foreach (var hold in due.Result)
            {
                ct.ThrowIfCancellationRequested();

                var released = await holds.ReleaseAsync(
                    new HoldSettlementRequest(
                        new HoldReference(hold.Chain, hold.HoldId),
                        // OI-25 — read this with PlatformHoldService.BuildContexts. Passing
                        // null does NOT mean "no tenant": the release falls back to the HOLD
                        // ROW's own tenant, so the payout is checked against that tenant's
                        // live grant from the beneficiary. That is deliberate and stays that
                        // way — the sweep must not be a second, quieter door that signs on a
                        // person's behalf with no grant behind it. Null is the sweep saying
                        // "I am nobody new"; the hold's own tenant remains the acting party,
                        // exactly as it was when the hold was opened.
                        ActingTenantId: null,
                        Reason: "the hold window elapsed and the hosted sweep released it"),
                    ct);

                if (released.IsError)
                {
                    _logger.LogWarning(
                        "The hold sweep could not release hold {HoldId} on {Chain}: {Detail}",
                        hold.HoldId, chain, released.Message);
                    continue;
                }

                if (released.Result is { Replayed: false })
                    _logger.LogInformation(
                        "The hold sweep released hold {HoldId} on {Chain}.", hold.HoldId, chain);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A cycle that throws must not take the worker down: the next cycle is the
            // recovery path, and a dead sweep is a hold window nobody keeps.
            _logger.LogError(ex, "A hold sweep cycle failed on {Chain}.", chain);
        }
    }
}
