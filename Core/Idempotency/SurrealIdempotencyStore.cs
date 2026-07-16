using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Idempotency;
using SurrealForge.Client.Query;
using PackageLedger = SurrealForge.Client.Idempotency.SurrealIdempotencyLedger;
using PackageOptions = SurrealForge.Client.Idempotency.IdempotencyLedgerOptions;
using PackageRecord = SurrealForge.Client.Idempotency.IdempotencyRecord;
using PackageState = SurrealForge.Client.Idempotency.IdempotencyState;

namespace AZOA.WebAPI.Core.Idempotency;

/// <summary>
/// SurrealDB-backed <see cref="IIdempotencyStore"/>. Thin adapter over the
/// package <see cref="PackageLedger"/> (which owns the claim/complete/fail/get
/// SurrealDB machinery, transient-conflict retry, and colon-key encoding) —
/// this type only maps between the package record shape and AZOA's domain
/// <see cref="IdempotencyRecord"/>. See <c>Core/Idempotency/AGENTS.md</c>.
/// </summary>
public sealed class SurrealIdempotencyStore : IIdempotencyStore
{
    private readonly PackageLedger _ledger;

    public SurrealIdempotencyStore(ISurrealExecutor executor)
    {
        // Preserve the historical AZOA behaviour: retry on SurrealDB 3.x
        // transient write-write conflicts, and base64url-encode colon-bearing
        // keys. Both are the package's config knobs, turned on here.
        _ledger = new PackageLedger(executor, new PackageOptions
        {
            RetryOnTransientConflict = true,
            EncodeColonKeys          = true,
        });
    }

    public async Task<IdempotencyClaim> TryClaimAsync(string key, string operationType, CancellationToken ct)
    {
        var claim = await _ledger.TryClaimAsync(key, operationType, ct);
        return new IdempotencyClaim(claim.Won, ToDomain(claim.Record));
    }

    public Task CompleteAsync(string key, string resultPayload, CancellationToken ct)
        => _ledger.CompleteAsync(key, resultPayload, ct);

    public Task FailAsync(string key, string error, CancellationToken ct)
        => _ledger.FailAsync(key, error, ct);

    public async Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
    {
        var record = await _ledger.GetAsync(key, ct);
        return record is null ? null : ToDomain(record);
    }

    /// <summary>Deterministic SurrealDB record id for a key (SHA-256 hex).</summary>
    public static string DeterministicId(string key) => PackageLedger.DeterministicId(key);

    /// <summary>Matches this adapter's configured colon-safe ledger key encoding.</summary>
    internal static string EncodeKeyForConfiguredLedger(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.IndexOf(':') < 0)
            return key;

        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "__sf_idem_b64__" + payload;
    }

    private static IdempotencyRecord ToDomain(PackageRecord r) => new()
    {
        Key           = r.Key,
        OperationType = r.OperationType,
        State         = r.State switch
        {
            PackageState.Completed => IdempotencyState.Completed,
            PackageState.Failed    => IdempotencyState.Failed,
            _                      => IdempotencyState.InProgress,
        },
        ResultPayload = r.ResultPayload,
        Error         = r.Error,
        CreatedAt     = r.CreatedAt,
        UpdatedAt     = r.UpdatedAt,
    };
}
