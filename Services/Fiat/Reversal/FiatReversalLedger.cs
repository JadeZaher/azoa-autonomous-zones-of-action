// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Core.Surreal;   // SurrealRecordParam (record<> binding, never a bare hex id)
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;   // SurrealTransientConflict (retryable 3.x txn conflicts)
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fiat.Reversal;

/// <summary>The provider event types the reversal pipeline claims. Nothing else moves a reversal.</summary>
public static class FiatReversalEventTypes
{
    /// <summary>The node (or the operator) gave the money back.</summary>
    public const string ChargeRefunded = "charge.refunded";

    /// <summary>The payer asked their bank to take the money back.</summary>
    public const string DisputeCreated = "charge.dispute.created";

    /// <summary>The provider has actually taken the money. This is what starts a clawback.</summary>
    public const string DisputeFundsWithdrawn = "charge.dispute.funds_withdrawn";

    /// <summary>The dispute went the node's way and the money came back.</summary>
    public const string DisputeFundsReinstated = "charge.dispute.funds_reinstated";

    /// <summary>Every type this pipeline handles, in the order a dispute walks them.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        ChargeRefunded,
        DisputeCreated,
        DisputeFundsWithdrawn,
        DisputeFundsReinstated,
    };

    /// <summary>True when this pipeline claims the type.</summary>
    public static bool IsHandled(string? eventType)
        => eventType is not null && All.Contains(eventType, StringComparer.Ordinal);
}

/// <summary>Which kind of reversal a row is. Mirrors the POCO allow-list.</summary>
public static class FiatReversalKinds
{
    /// <summary>Money the node gave back.</summary>
    public const string Refund = "Refund";

    /// <summary>Money a payer's bank took back.</summary>
    public const string Dispute = "Dispute";
}

/// <summary>Durable states of one reversal. Mirrors the POCO allow-list.</summary>
public static class FiatReversalStatuses
{
    /// <summary>Recorded; the money has not been taken yet (a dispute nobody has debited).</summary>
    public const string Open = "Open";

    /// <summary>The money is gone and the compensation ladder is running.</summary>
    public const string Recovering = "Recovering";

    /// <summary>Everything the reversal took back was recovered. Terminal.</summary>
    public const string Recovered = "Recovered";

    /// <summary>Some was recovered and the rest was absorbed. Terminal.</summary>
    public const string PartiallyRecovered = "PartiallyRecovered";

    /// <summary>Nothing was recoverable; the platform is out of pocket for the whole of it. Terminal.</summary>
    public const string Absorbed = "Absorbed";

    /// <summary>The provider gave the money back. Terminal unless units were already clawed back.</summary>
    public const string Reinstated = "Reinstated";

    /// <summary>No issuance could be correlated, so there is nothing to unwind. Terminal.</summary>
    public const string Unmatched = "Unmatched";

    /// <summary>Only a person can move this on. The reason is always on the row.</summary>
    public const string NeedsOperator = "NeedsOperator";

    /// <summary>True for a status no further automatic step will change.</summary>
    public static bool IsSettled(string? status) =>
        status is Recovered or PartiallyRecovered or Absorbed or Unmatched or Reinstated;

    /// <summary>True for a status that is waiting on a person rather than on the pipeline.</summary>
    public static bool NeedsPerson(string? status) =>
        string.Equals(status, NeedsOperator, StringComparison.Ordinal);
}

/// <summary>Which rung of the compensation ladder answered. Mirrors the POCO allow-list.</summary>
public static class FiatReversalRecoveryPaths
{
    /// <summary>No rung has run.</summary>
    public const string None = "None";

    /// <summary>The units were still in the payer's own account.</summary>
    public const string Balance = "Balance";

    /// <summary>An open hold the reversed money had funded was given back.</summary>
    public const string Hold = "Hold";

    /// <summary>The units the reversed money had bought were taken back.</summary>
    public const string Purchase = "Purchase";

    /// <summary>Nothing was recoverable and the platform absorbed the loss.</summary>
    public const string Reserve = "Reserve";

    /// <summary>More than one rung contributed.</summary>
    public const string Mixed = "Mixed";

    /// <summary>Folds a newly-answered rung into the path already recorded.</summary>
    public static string Combine(string? existing, string added)
    {
        if (string.IsNullOrWhiteSpace(existing) || string.Equals(existing, None, StringComparison.Ordinal))
            return added;
        if (string.Equals(existing, added, StringComparison.Ordinal))
            return existing!;
        return Mixed;
    }
}

/// <summary>Outcome of a create-if-absent claim: the row, plus whether this caller created it.</summary>
public sealed record FiatReversalClaim(FiatReversal Row, bool Created);

/// <summary>
/// The durable record of every reversal of a card payment, and the read side of the account
/// risk flag. See <c>Services/Fiat/Reversal/AGENTS.md</c>.
/// </summary>
public interface IFiatReversalLedger
{
    /// <summary>Creates the reversal only if it is absent. A losing racer gets the winner's row.</summary>
    Task<AZOAResult<FiatReversalClaim>> ClaimAsync(FiatReversal candidate, CancellationToken ct = default);

    /// <summary>One reversal by record id; a null result when there is none.</summary>
    Task<AZOAResult<FiatReversal?>> GetAsync(string recordId, CancellationToken ct = default);

    /// <summary>
    /// Compare-and-swap write: lands only while the persisted <c>version</c> is still
    /// <paramref name="expectedVersion"/>. A lost race is an error, never a silent overwrite.
    /// </summary>
    Task<AZOAResult<FiatReversal>> UpdateIfVersionAsync(
        FiatReversal row, long expectedVersion, CancellationToken ct = default);

    /// <summary>
    /// Reversals that are not settled yet, oldest first. The operator's list of money the node
    /// has had taken back and has not finished unwinding.
    /// </summary>
    Task<AZOAResult<IReadOnlyList<FiatReversal>>> ListOpenAsync(
        int limit, CancellationToken ct = default);

    /// <summary>Every reversal recorded against one account, newest first.</summary>
    Task<AZOAResult<IReadOnlyList<FiatReversal>>> ListForUserAsync(
        Guid userId, int limit, CancellationToken ct = default);

    /// <summary>
    /// True while <paramref name="userId"/> carries at least one UNCLEARED risk flag. This is
    /// the read side of the flag written by the compensation ladder, and
    /// <see cref="RiskHoldValueGate"/> is the caller that turns it into a refusal.
    /// </summary>
    Task<AZOAResult<bool>> HasActiveFlagAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>
/// SurrealDB-backed reversal ledger. Colocated with the pipeline because
/// <c>Providers/Stores/Surreal/</c> is outside this item's file boundary; the interface is
/// <see cref="IFiatReversalLedger"/>, so moving it later is a file move.
/// </summary>
public sealed class SurrealFiatReversalLedger : IFiatReversalLedger
{
    private const string Table = FiatReversal.SchemaNameConst;

    private const string OpenStatement =
        "SELECT * FROM fiat_reversal WHERE status NOTINSIDE $_settled ORDER BY opened_at ASC LIMIT $_limit";

    private const string ForUserStatement =
        "SELECT * FROM fiat_reversal WHERE subject_user_id = $_user ORDER BY opened_at DESC LIMIT $_limit";

    private const string ActiveFlagStatement =
        "SELECT * FROM fiat_reversal WHERE subject_user_id = $_user AND account_flagged = true LIMIT 1";

    private static readonly string[] SettledStatuses =
    {
        FiatReversalStatuses.Recovered,
        FiatReversalStatuses.PartiallyRecovered,
        FiatReversalStatuses.Absorbed,
        FiatReversalStatuses.Unmatched,
        FiatReversalStatuses.Reinstated,
    };

    private readonly ISurrealExecutor _executor;

    public SurrealFiatReversalLedger(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatReversalClaim>> ClaimAsync(
        FiatReversal candidate, CancellationToken ct = default)
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

            var created = response.GetValues<FiatReversal>(0).SingleOrDefault() ?? candidate;
            return AZOAResult<FiatReversalClaim>.Success(
                new FiatReversalClaim(created, Created: true), "Created.");
        }
        catch (Exception ex)
        {
            // A CREATE that lost to an existing row is the second delivery of one reversal, not a
            // fault -- but only when the row really is there now. Re-read before concluding that,
            // so a transport failure is never reported as "this reversal is already recorded"
            // (which would leave money taken back with nothing unwinding it).
            var existing = await GetAsync(candidate.Id, ct);
            if (!existing.IsError && existing.Result is not null)
                return AZOAResult<FiatReversalClaim>.Success(
                    new FiatReversalClaim(existing.Result, Created: false), "Already recorded.");

            return new AZOAResult<FiatReversalClaim>()
                .CaptureException(ex, $"Reversal ledger write failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatReversal?>> GetAsync(string recordId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordId))
            return new AZOAResult<FiatReversal?> { Result = null, Message = "Not found." };

        try
        {
            var query = SurrealQuery<FiatReversal>.Key(recordId);
            var row = await _executor.QuerySingleAsync<FiatReversal>(query, ct);
            return new AZOAResult<FiatReversal?>
            {
                Result = row,
                Message = row is null ? "Not found." : "Success",
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<FiatReversal?>()
                .CaptureException(ex, $"Reversal ledger read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatReversal>> UpdateIfVersionAsync(
        FiatReversal row, long expectedVersion, CancellationToken ct = default)
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
                return AZOAResult<FiatReversal>.Failure(
                    $"Reversal '{row.Id}' changed underneath this update "
                    + $"(expected version {expectedVersion}).");

            return AZOAResult<FiatReversal>.Success(
                response.GetValues<FiatReversal>(0).SingleOrDefault() ?? row, "Saved.");
        }
        catch (Exception ex)
        {
            return new AZOAResult<FiatReversal>()
                .CaptureException(ex, $"Reversal ledger update failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<FiatReversal>>> ListOpenAsync(
        int limit, CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of(OpenStatement)
                .WithParam("_settled", SettledStatuses)
                .WithParam("_limit", Math.Clamp(limit, 1, 500));

            var rows = await _executor.QueryAsync<FiatReversal>(query, ct);
            return AZOAResult<IReadOnlyList<FiatReversal>>.Success(
                rows?.ToList() ?? new List<FiatReversal>(), "Success");
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<FiatReversal>>()
                .CaptureException(ex, $"Reversal ledger read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<FiatReversal>>> ListForUserAsync(
        Guid userId, int limit, CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
            return AZOAResult<IReadOnlyList<FiatReversal>>.Success(Array.Empty<FiatReversal>(), "Success");

        try
        {
            // subject_user_id is a record<user> column, so the parameter has to be bound as a
            // native record id -- a bare string compares equal to nothing and would answer "this
            // person has no reversals" with no error at all.
            var query = SurrealQuery
                .Of(ForUserStatement)
                .WithParam("_user", SurrealRecordParam.Of(User.SchemaNameConst, userId.ToString("N")))
                .WithParam("_limit", Math.Clamp(limit, 1, 500));

            var rows = await _executor.QueryAsync<FiatReversal>(query, ct);
            return AZOAResult<IReadOnlyList<FiatReversal>>.Success(
                rows?.ToList() ?? new List<FiatReversal>(), "Success");
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<FiatReversal>>()
                .CaptureException(ex, $"Reversal ledger read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> HasActiveFlagAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
            return AZOAResult<bool>.Success(false, "Success");

        try
        {
            var query = SurrealQuery
                .Of(ActiveFlagStatement)
                .WithParam("_user", SurrealRecordParam.Of(User.SchemaNameConst, userId.ToString("N")));

            var rows = await _executor.QueryAsync<FiatReversal>(query, ct);
            return AZOAResult<bool>.Success(rows?.Any() == true, "Success");
        }
        catch (Exception ex)
        {
            // FAIL CLOSED is the caller's job, not this one's: an unreadable ledger is an error,
            // and RiskHoldValueGate refuses on an error rather than reading a default of false.
            return new AZOAResult<bool>()
                .CaptureException(ex, $"Reversal flag read failed: {ex.Message}");
        }
    }
}
