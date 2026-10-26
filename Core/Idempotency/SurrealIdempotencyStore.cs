using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;
using SurrealTransientConflict = SurrealForge.Client.Idempotency.SurrealTransientConflict;
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
public sealed class SurrealIdempotencyStore : IIdempotencyStore, IAllocationRecoveryStore, IValueMoveReceiptStore
{
    private readonly PackageLedger _ledger;
    private readonly ISurrealExecutor _executor;
    public const string NoValueEffectProof = "allocation:no-value-effect:v1";
    public const string RefundReservationType = "on-ramp-refund-reservation";

    public SurrealIdempotencyStore(ISurrealExecutor executor)
    {
        _executor = executor;
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

    /// <inheritdoc/>
    public async Task RecordNoEffectFailureAsync(string key, string safeError, CancellationToken ct)
    {
        var query = SurrealWriter.UpdateOnly<IdempotencyKeyStore>(DeterministicId(key))
            .Where(row => row.State == IdempotencyKeyStore.StateKind.InProgress && row.OperationType == "fiat_allocation")
            .Set(row => row.State, IdempotencyKeyStore.StateKind.Failed)
            .Set(row => row.Error, safeError)
            .Set(row => row.EffectDisposition, NoValueEffectProof)
            .Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build();
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () => {
            var result = await _executor.ExecuteAsync(query, ct); result.EnsureAllOk(); return result;
        }, ct);
        if (response.GetValues<IdempotencyKeyStore>(0).Count != 1)
            throw new InvalidOperationException("Allocation ownership changed before its no-effect failure was recorded.");
    }

    /// <inheritdoc/>
    public async Task<bool> TryReserveRefundAfterNoEffectAsync(string key, CancellationToken ct)
    {
        var query = SurrealWriter.UpdateOnly<IdempotencyKeyStore>(DeterministicId(key))
            .Where(row => row.State == IdempotencyKeyStore.StateKind.Failed && row.OperationType == "fiat_allocation"
                && row.EffectDisposition == NoValueEffectProof)
            .Set(row => row.OperationType, RefundReservationType)
            .Set(row => row.Error, "Issuance permanently reserved for an operator refund.")
            .Unset(row => row.EffectDisposition)
            .Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build();
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () => {
            var result = await _executor.ExecuteAsync(query, ct); result.EnsureAllOk(); return result;
        }, ct);
        return response.GetValues<IdempotencyKeyStore>(0).Count == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> TryReopenAfterNoEffectAsync(string key, CancellationToken ct)
    {
        // Same predicate as the refund reservation, so reopen and refund stay mutually exclusive.
        var query = SurrealWriter.DeleteOnly<IdempotencyKeyStore>(DeterministicId(key))
            .Where(row => row.State == IdempotencyKeyStore.StateKind.Failed && row.OperationType == "fiat_allocation"
                && row.EffectDisposition == NoValueEffectProof).Build();
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () => {
            var result = await _executor.ExecuteAsync(query, ct); result.EnsureAllOk(); return result;
        }, ct);
        return response.GetValues<IdempotencyKeyStore>(0).Count == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> TryPrepareAsync(string key, string preparedJson, CancellationToken ct, long expectedGeneration = 0)
    {
        var query = SurrealWriter.UpdateOnly<IdempotencyKeyStore>(DeterministicId(key))
            .Where(row => row.State == IdempotencyKeyStore.StateKind.InProgress && row.OperationType == "fiat-off-ramp-prepared"
                && row.PreparedTransactionJson == null && row.PreparationGeneration == expectedGeneration)
            .Set(row => row.PreparedTransactionJson, preparedJson).Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build();
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () => {
            var result = await _executor.ExecuteAsync(query, ct); result.EnsureAllOk(); return result;
        }, ct);
        return response.GetValues<IdempotencyKeyStore>(0).Count == 1;
    }

    /// <inheritdoc/>
    public async Task<string?> GetPreparedAsync(string key, CancellationToken ct)
        => (await _executor.QuerySingleAsync<IdempotencyKeyStore>(SurrealQuery<IdempotencyKeyStore>.Key(DeterministicId(key)), ct))?.PreparedTransactionJson;

    /// <inheritdoc/>
    public async Task<long> GetPreparationGenerationAsync(string key, CancellationToken ct)
        => (await _executor.QuerySingleAsync<IdempotencyKeyStore>(SurrealQuery<IdempotencyKeyStore>.Key(DeterministicId(key)), ct))?.PreparationGeneration
            ?? throw new InvalidOperationException("Prepared value claim is missing.");

    /// <inheritdoc/>
    public async Task<bool> TryClearPreparedAsync(string key, string expectedPreparedJson, CancellationToken ct)
    {
        var generation = await GetPreparationGenerationAsync(key, ct);
        var query = SurrealWriter.UpdateOnly<IdempotencyKeyStore>(DeterministicId(key))
            .Where(row => row.State == IdempotencyKeyStore.StateKind.InProgress && row.OperationType == "fiat-off-ramp-prepared"
                && row.PreparedTransactionJson == expectedPreparedJson && row.PreparationGeneration == generation)
            .Unset(row => row.PreparedTransactionJson).Set(row => row.PreparationGeneration, checked(generation + 1))
            .Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build();
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () => {
            var result = await _executor.ExecuteAsync(query, ct); result.EnsureAllOk(); return result;
        }, ct);
        return response.GetValues<IdempotencyKeyStore>(0).Count == 1;
    }

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
