// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Core.Surreal;   // SurrealRecordParam (record<> binding, never a bare hex id)
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;   // SurrealTransientConflict (retryable 3.x txn conflicts)
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fiat.OnRamp;

/// <summary>Durable states of one on-ramp lot. Mirrors the POCO allow-list.</summary>
public static class OnRampLotStatuses
{
    /// <summary>The money cleared; the units are not confirmed in the person's account yet.</summary>
    public const string Pending = "Pending";

    /// <summary>The units are in the person's account. This is the only status W8.1 may consume.</summary>
    public const string Issued = "Issued";

    /// <summary>The node decided not to credit. The money is owed back.</summary>
    public const string Refused = "Refused";

    /// <summary>The node ran out of attempts without establishing the outcome. Reconcile, then decide.</summary>
    public const string Stuck = "Stuck";

    /// <summary>A refund or dispute took the money back and the units with it (W4.3).</summary>
    public const string Reversed = "Reversed";

    /// <summary>True for a status that will never become <see cref="Issued"/> without a person acting.</summary>
    public static bool NeedsOperator(string? status) =>
        string.Equals(status, Refused, StringComparison.Ordinal)
        || string.Equals(status, Stuck, StringComparison.Ordinal);
}

/// <summary>Outcome of a create-if-absent claim: the row, plus whether this caller created it.</summary>
public sealed record OnRampLotClaim(OnRampLot Row, bool Created);

/// <summary>
/// Per-issuance provenance for fiat-funded units: the only record of WHICH units a card bought.
/// See <c>Services/Fiat/OnRamp/AGENTS.md</c> §lots.
/// </summary>
public interface IOnRampLotLedger
{
    /// <summary>Creates the lot only if it is absent. A losing racer gets the winner's row.</summary>
    Task<AZOAResult<OnRampLotClaim>> ClaimAsync(OnRampLot candidate, CancellationToken ct = default);

    /// <summary>The lot one funding intent settled into, or a null result when there is none.</summary>
    Task<AZOAResult<OnRampLot?>> GetByIntentAsync(string intentId, CancellationToken ct = default);

    /// <summary>
    /// Compare-and-swap write: lands only while the persisted <c>version</c> is still
    /// <paramref name="expectedVersion"/>. A lost race is an error, never a silent overwrite.
    /// </summary>
    Task<AZOAResult<OnRampLot>> UpdateIfVersionAsync(
        OnRampLot row, long expectedVersion, CancellationToken ct = default);

    /// <summary>
    /// One person's issued lots, oldest first — the FIFO order W8.1's source-aged cash-out
    /// consumes. Reversed, refused and pending lots are excluded: none of them is spendable
    /// provenance.
    /// </summary>
    Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListIssuedForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Lots that need a person: refused or stuck, newest first. The operator surface for
    /// "somebody was charged and not credited".
    /// </summary>
    Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListNeedingOperatorAsync(
        int limit, CancellationToken ct = default);
}

/// <summary>
/// SurrealDB-backed lot ledger. Colocated with the saga because
/// <c>Providers/Stores/Surreal/</c> is outside this item's file boundary; the interface is
/// <see cref="IOnRampLotLedger"/>, so moving it later is a file move.
/// </summary>
public sealed class SurrealOnRampLotLedger : IOnRampLotLedger
{
    private const string Table = OnRampLot.SchemaNameConst;

    private const string IssuedForUserStatement =
        "SELECT * FROM on_ramp_lot WHERE subject_user_id = $_user AND status = $_status "
        + "ORDER BY issued_at ASC";

    private const string NeedingOperatorStatement =
        "SELECT * FROM on_ramp_lot WHERE status INSIDE $_statuses ORDER BY created_at DESC LIMIT $_limit";

    private readonly ISurrealExecutor _executor;

    public SurrealOnRampLotLedger(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<OnRampLotClaim>> ClaimAsync(
        OnRampLot candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        try
        {
            var create = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", candidate.Id)
                .WithParam("_body", candidate);

            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(create, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);

            var created = response.GetValues<OnRampLot>(0).SingleOrDefault() ?? candidate;
            return AZOAResult<OnRampLotClaim>.Success(new OnRampLotClaim(created, Created: true), "Created.");
        }
        catch (Exception ex)
        {
            // A CREATE that lost to an existing row is the redelivery case, not a fault — but only
            // when the row really is there now. Re-read before concluding that, so a transport
            // failure is never reported as "this payment already has a lot" (which would leave a
            // person charged with nothing credited and nothing to retry).
            var existing = await GetAsync(candidate.Id, ct);
            if (!existing.IsError && existing.Result is not null)
                return AZOAResult<OnRampLotClaim>.Success(
                    new OnRampLotClaim(existing.Result, Created: false), "Already claimed.");

            return new AZOAResult<OnRampLotClaim>()
                .CaptureException(ex, $"On-ramp lot write failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public Task<AZOAResult<OnRampLot?>> GetByIntentAsync(string intentId, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(intentId)
            ? Task.FromResult(new AZOAResult<OnRampLot?> { Result = null, Message = "Not found." })
            : GetAsync(OnRampLot.RecordIdFor(intentId), ct);

    /// <inheritdoc />
    public async Task<AZOAResult<OnRampLot>> UpdateIfVersionAsync(
        OnRampLot row, long expectedVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        try
        {
            var update = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) CONTENT $_body WHERE version = $_expected RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", row.Id)
                .WithParam("_body", row)
                .WithParam("_expected", expectedVersion);

            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(update, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);

            if (response.Count == 0 || !response[0].IsOk || response[0].AffectedCount() != 1)
                return AZOAResult<OnRampLot>.Failure(
                    $"On-ramp lot '{row.Id}' changed underneath this update "
                    + $"(expected version {expectedVersion}).");

            return AZOAResult<OnRampLot>.Success(
                response.GetValues<OnRampLot>(0).SingleOrDefault() ?? row, "Saved.");
        }
        catch (Exception ex)
        {
            return new AZOAResult<OnRampLot>()
                .CaptureException(ex, $"On-ramp lot update failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListIssuedForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
            return AZOAResult<IReadOnlyList<OnRampLot>>.Success(Array.Empty<OnRampLot>(), "Success");

        try
        {
            // subject_user_id is a record<user> column, so the parameter has to be bound as a
            // native record id — a bare string compares equal to nothing and would answer "this
            // person has no on-ramped units" with no error at all.
            var query = SurrealQuery
                .Of(IssuedForUserStatement)
                .WithParam("_user", SurrealRecordParam.Of(User.SchemaNameConst, userId.ToString("N")))
                .WithParam("_status", OnRampLotStatuses.Issued);

            var rows = await _executor.QueryAsync<OnRampLot>(query, ct);
            return AZOAResult<IReadOnlyList<OnRampLot>>.Success(
                rows?.ToList() ?? new List<OnRampLot>(), "Success");
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<OnRampLot>>()
                .CaptureException(ex, $"On-ramp lot read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListNeedingOperatorAsync(
        int limit, CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of(NeedingOperatorStatement)
                .WithParam("_statuses", new[] { OnRampLotStatuses.Refused, OnRampLotStatuses.Stuck })
                .WithParam("_limit", Math.Clamp(limit, 1, 500));

            var rows = await _executor.QueryAsync<OnRampLot>(query, ct);
            return AZOAResult<IReadOnlyList<OnRampLot>>.Success(
                rows?.ToList() ?? new List<OnRampLot>(), "Success");
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<OnRampLot>>()
                .CaptureException(ex, $"On-ramp lot read failed: {ex.Message}");
        }
    }

    private async Task<AZOAResult<OnRampLot?>> GetAsync(string recordId, CancellationToken ct)
    {
        try
        {
            var query = SurrealQuery<OnRampLot>.Key(recordId);
            var row = await _executor.QuerySingleAsync<OnRampLot>(query, ct);
            return new AZOAResult<OnRampLot?> { Result = row, Message = row is null ? "Not found." : "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<OnRampLot?>()
                .CaptureException(ex, $"On-ramp lot read failed: {ex.Message}");
        }
    }
}
