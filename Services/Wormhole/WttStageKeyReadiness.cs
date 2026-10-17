using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Keyring readiness checks and CAS rewrap maintenance; see AGENTS.md §Protected protocol stage keys.</summary>
public sealed class WttStageKeyReadiness(IWttStageKeyStore store, IWttStageKeyProtector protector,
    WttStageKeyReadinessState? state = null, TimeProvider? clock = null)
{
    private const int RewrapBatchLimit = 100;

    /// <summary>Throws when a retained envelope would become unreadable; the retirement precondition.</summary>
    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (!protector.IsConfigured || await store.FindUnavailableKeyAsync(protector.AvailableKeyVersions, ct) is not null)
            throw new InvalidOperationException("A retained WTT stage encryption key version is missing; restore it or rewrap before retirement.");
    }

    /// <summary>Evaluates the keyring against every retained envelope and publishes the degrade signal.</summary>
    public async Task<WttStageKeyReadinessReport> CheckAsync(CancellationToken ct = default)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        WttStageKeyReadinessReport report;
        if (!protector.IsConfigured)
            report = new("Degraded", false, null, [], null, false,
                protector.ConfigurationError ?? "The Wtt:StageKeys keyring is not configured.", now);
        else
        {
            var versions = protector.AvailableKeyVersions.Order(StringComparer.Ordinal).ToArray();
            var unreadable = await store.FindUnavailableKeyAsync(versions, ct);
            var pending = await store.FindNotOnVersionAsync(protector.ActiveKeyId, 1, ct);
            report = unreadable is not null
                ? new("Degraded", true, protector.ActiveKeyId, versions, unreadable.Id, pending.Count > 0,
                    "A retained stage key references an unavailable encryption version; restore that key on every host.", now)
                : new("Ready", true, protector.ActiveKeyId, versions, null, pending.Count > 0,
                    pending.Count > 0 ? "Ready; older encryption versions remain until rewrap completes." : "Ready.", now);
        }
        state?.Publish(report);
        return report;
    }

    /// <summary>Re-encrypts up to one batch of readable envelopes onto the active key by exact-envelope CAS; signs nothing.</summary>
    public async Task<WttStageKeyRewrapReport> RewrapAsync(CancellationToken ct = default)
    {
        if (!protector.IsConfigured) return new(0, 0, false, await CheckAsync(ct));
        int rewrapped = 0, conflicted = 0;
        foreach (var row in await store.FindNotOnVersionAsync(protector.ActiveKeyId, RewrapBatchLimit, ct))
        {
            if (!protector.AvailableKeyVersions.Contains(row.KeyVersion, StringComparer.Ordinal)) continue;
            var binding = JsonSerializer.Deserialize<WttStageKeyBinding>(row.BindingJson)
                ?? throw new InvalidOperationException("WTT stage key binding is missing.");
            var envelope = protector.Rewrap(binding, row.PublicKey, Envelope(row));
            if (await store.TryRewrapAsync(row, envelope.KeyVersion, JsonSerializer.Serialize(envelope), ct)) rewrapped++;
            else conflicted++;
        }
        var readiness = await CheckAsync(ct);
        return new(rewrapped, conflicted, readiness.RewrapPending, readiness);
    }

    private static WttProtectedStageKey Envelope(WttStageKeyEnvelope row)
    {
        var envelope = JsonSerializer.Deserialize<WttProtectedStageKey>(row.EnvelopeJson)
            ?? throw new InvalidOperationException("WTT stage key envelope is missing.");
        if (envelope.KeyVersion != row.KeyVersion) throw new InvalidOperationException("WTT stage encryption key version is inconsistent.");
        return envelope;
    }
}

/// <summary>Process-wide last readiness verdict; unknown until the first check, which callers treat as degraded.</summary>
public sealed class WttStageKeyReadinessState
{
    private WttStageKeyReadinessReport? _current;

    public WttStageKeyReadinessReport? Current => Volatile.Read(ref _current);

    public bool Ready => Current?.Ready == true;

    public void Publish(WttStageKeyReadinessReport report) => Volatile.Write(ref _current, report);
}

/// <summary>Re-checks keyring readiness at startup and on a fixed cadence so a restored key lifts the degrade without a restart.</summary>
public sealed class WttStageKeyReadinessWorker(IServiceScopeFactory scopes, ILogger<WttStageKeyReadinessWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var report = await scope.ServiceProvider.GetRequiredService<WttStageKeyReadiness>().CheckAsync(stoppingToken);
                if (!report.Configured) logger.LogWarning("WTT stage-key keyring is not configured; stage-key WTT routes stay refused: {Detail}", report.Detail);
                else if (!report.Ready) logger.LogCritical("WTT stage-key keyring degraded; new stage-key admissions are refused: {Detail}", report.Detail);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A database outage leaves the last verdict in place; the next tick re-evaluates.
                logger.LogError(exception, "WTT stage-key readiness check failed.");
            }
            await Task.Delay(Cadence, stoppingToken);
        }
    }
}
