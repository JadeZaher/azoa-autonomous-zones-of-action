using Azoa.SurrealDb.Client.Query;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Idempotency;
using PkgIdempotency = Azoa.SurrealDb.Client.Idempotency;

namespace AZOA.WebAPI.Core.Idempotency;

/// <summary>
/// SurrealDB-backed <see cref="IIdempotencyStore"/>.
///
/// This type is a THIN ADAPTER. All SurrealDB work — the insert-wins claim,
/// the UNIQUE-violation race handling, the conditional state-transition
/// UPDATEs, and the deterministic record-id encoding — lives in the reusable
/// package primitive
/// <see cref="Azoa.SurrealDb.Client.Idempotency.SurrealIdempotencyLedger"/>.
/// This class only:
/// <list type="bullet">
///   <item>implements the WebAPI domain contract
///         <see cref="IIdempotencyStore"/>;</item>
///   <item>maps between the package record/state types and the WebAPI domain
///         <see cref="IdempotencyRecord"/> / <see cref="IdempotencyState"/> /
///         <see cref="IdempotencyClaim"/>.</item>
/// </list>
///
/// The public behaviour is identical to the prior hand-rolled implementation;
/// see <see cref="Azoa.SurrealDb.Client.Idempotency.SurrealIdempotencyLedger"/>
/// for the atomicity model and state-transition guard documentation.
/// </summary>
public sealed class SurrealIdempotencyStore : IIdempotencyStore
{
    private const string Table = "idempotency_key_store";

    private readonly PkgIdempotency.SurrealIdempotencyLedger _ledger;

    public SurrealIdempotencyStore(ISurrealExecutor executor)
    {
        if (executor is null) throw new ArgumentNullException(nameof(executor));
        _ledger = new PkgIdempotency.SurrealIdempotencyLedger(executor, Table);
    }

    /// <inheritdoc />
    public async Task<IdempotencyClaim> TryClaimAsync(
        string key,
        string operationType,
        CancellationToken ct)
    {
        var claim = await _ledger.TryClaimAsync(key, operationType, ct);
        return new IdempotencyClaim(claim.Won, ToDomain(claim.Record));
    }

    /// <inheritdoc />
    public Task CompleteAsync(string key, string resultPayload, CancellationToken ct)
        => _ledger.CompleteAsync(key, resultPayload, ct);

    /// <inheritdoc />
    public Task FailAsync(string key, string error, CancellationToken ct)
        => _ledger.FailAsync(key, error, ct);

    /// <inheritdoc />
    public async Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
    {
        var record = await _ledger.GetAsync(key, ct);
        return record is not null ? ToDomain(record) : null;
    }

    /// <summary>
    /// Derives the SurrealDB record id from an idempotency key. Delegates to the
    /// package primitive so the encoding stays in one place.
    /// </summary>
    public static string DeterministicId(string key)
        => PkgIdempotency.SurrealIdempotencyLedger.DeterministicId(key);

    // ── Mapping: package record/state → WebAPI domain record/state ────────────

    private static IdempotencyRecord ToDomain(PkgIdempotency.IdempotencyRecord r) => new()
    {
        Key           = r.Key,
        OperationType = r.OperationType,
        State         = ToDomain(r.State),
        ResultPayload = r.ResultPayload,
        Error         = r.Error,
        CreatedAt     = r.CreatedAt,
        UpdatedAt     = r.UpdatedAt,
    };

    private static IdempotencyState ToDomain(PkgIdempotency.IdempotencyState state) => state switch
    {
        PkgIdempotency.IdempotencyState.InProgress => IdempotencyState.InProgress,
        PkgIdempotency.IdempotencyState.Completed  => IdempotencyState.Completed,
        PkgIdempotency.IdempotencyState.Failed     => IdempotencyState.Failed,
        _                                          => IdempotencyState.InProgress
    };
}
