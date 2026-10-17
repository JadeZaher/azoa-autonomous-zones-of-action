// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
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

    public const string RefundPending = "RefundPending";
    public const string Refunded = "Refunded";

    /// <summary>
    /// The statuses that will never become <see cref="Issued"/> without a person acting.
    /// </summary>
    /// <remarks>Exposed read-only: a shared mutable array would let one caller silently widen or
    /// narrow what the settlement handler's short-circuit and the operator read agree on.</remarks>
    public static readonly IReadOnlyList<string> NeedingOperatorStatuses = new[] { Refused, Stuck };

    /// <summary>True for a status that will never become <see cref="Issued"/> without a person acting.</summary>
    public static bool NeedsOperator(string? status) =>
        status is not null && NeedingOperatorStatuses.Contains(status, StringComparer.Ordinal);
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
    /// Lots that need a person, newest first: refused, stuck, or still pending after the
    /// provider's own retry budget has run out. The operator surface for "somebody was charged
    /// and not credited".
    /// </summary>
    /// <remarks>
    /// The pending arm is not decoration. A lot is CREATED the moment the payment is confirmed
    /// cleared, so every row here means money was taken; an attempt that fails without reaching a
    /// decision leaves the lot Pending and answers the provider 500, and once the provider stops
    /// retrying that lot never becomes Refused or Stuck on its own. Listing only
    /// <see cref="OnRampLotStatuses.NeedsOperator"/> would therefore hide the most common way a
    /// charge goes uncredited. The two ideas stay separate on purpose: NeedsOperator drives the
    /// settlement handler's stop-retrying short-circuit and must stay narrow, while this list is
    /// the board and has to be complete.
    /// </remarks>
    Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListNeedingOperatorAsync(
        int limit, CancellationToken ct = default);
}

/// <summary>
/// SurrealDB-backed lot ledger. Colocated with the saga because
/// <c>Providers/Stores/Surreal/</c> is outside this item's file boundary; the interface is
/// <see cref="IOnRampLotLedger"/>, so moving it later is a file move.
/// </summary>
public sealed class SurrealOnRampLotLedger(ISurrealExecutor executor) : IOnRampLotLedger
{
    public static readonly TimeSpan StrandedPendingAfter = TimeSpan.FromHours(72);

    /// <inheritdoc/>
    public async Task<AZOAResult<OnRampLotClaim>> ClaimAsync(OnRampLot candidate, CancellationToken ct = default)
    {
        try
        {
            var response = await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct);
            response.EnsureAllOk();
            return AZOAResult<OnRampLotClaim>.Success(new(response.GetValues<OnRampLot>(0).Single(), true));
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var existing = await executor.QuerySingleAsync<OnRampLot>(SurrealQuery<OnRampLot>.Key(candidate.Id), ct);
            if (existing is null) throw;
            return AZOAResult<OnRampLotClaim>.Success(new(existing, false));
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<OnRampLot?>> GetByIntentAsync(string intentId, CancellationToken ct = default)
        => new() { Result = string.IsNullOrWhiteSpace(intentId) ? null
            : await executor.QuerySingleAsync<OnRampLot>(SurrealQuery<OnRampLot>.Key(OnRampLot.RecordIdFor(SurrealLink.FromLink(intentId)!)), ct) };

    /// <inheritdoc/>
    public async Task<AZOAResult<OnRampLot>> UpdateIfVersionAsync(OnRampLot row, long expectedVersion, CancellationToken ct = default)
    {
        var update = SurrealWriter.UpdateOnly<OnRampLot>(row.Id)
            .Where(lot => lot.Version == expectedVersion)
            .Set(lot => lot.QuantityBaseUnits, row.QuantityBaseUnits)
            .Set(lot => lot.GrossQuantityBaseUnits, row.GrossQuantityBaseUnits)
            .Set(lot => lot.NodeFeeBaseUnits, row.NodeFeeBaseUnits)
            .Set(lot => lot.ConsumedBaseUnits, row.ConsumedBaseUnits)
            .Set(lot => lot.ReversedBaseUnits, row.ReversedBaseUnits)
            .Set(lot => lot.ReversalMarksJson, row.ReversalMarksJson)
            .Set(lot => lot.PriceScheduleVersion, row.PriceScheduleVersion)
            .Set(lot => lot.UnitsPerMinorUnit, row.UnitsPerMinorUnit)
            .Set(lot => lot.Status, row.Status)
            .Set(lot => lot.AttemptCount, row.AttemptCount)
            .Set(lot => lot.Version, row.Version)
            .Set(lot => lot.UpdatedAt, row.UpdatedAt)
            .Set(lot => lot.RecoveryAuditJson, row.RecoveryAuditJson);
        update = update.Set(lot => lot.RefundAttempt, row.RefundAttempt)
            .Set(lot => lot.RefundedMinorUnits, row.RefundedMinorUnits);
        update = row.PriceScheduleId is null ? update.Unset(lot => lot.PriceScheduleId) : update.Set(lot => lot.PriceScheduleId, row.PriceScheduleId);
        update = row.PricingScopeKey is null ? update.Unset(lot => lot.PricingScopeKey) : update.Set(lot => lot.PricingScopeKey, row.PricingScopeKey);
        update = row.SourceCheckoutSessionId is null ? update.Unset(lot => lot.SourceCheckoutSessionId) : update.Set(lot => lot.SourceCheckoutSessionId, row.SourceCheckoutSessionId);
        update = row.SourcePaymentIntentId is null ? update.Unset(lot => lot.SourcePaymentIntentId) : update.Set(lot => lot.SourcePaymentIntentId, row.SourcePaymentIntentId);
        update = row.SourceChargeId is null ? update.Unset(lot => lot.SourceChargeId) : update.Set(lot => lot.SourceChargeId, row.SourceChargeId);
        update = row.StatusReason is null ? update.Unset(lot => lot.StatusReason) : update.Set(lot => lot.StatusReason, row.StatusReason);
        update = row.OperationId is null ? update.Unset(lot => lot.OperationId) : update.Set(lot => lot.OperationId, row.OperationId);
        update = row.IssuedAt is null ? update.Unset(lot => lot.IssuedAt) : update.Set(lot => lot.IssuedAt, row.IssuedAt);
        update = row.ReversalWindowEnds is null ? update.Unset(lot => lot.ReversalWindowEnds) : update.Set(lot => lot.ReversalWindowEnds, row.ReversalWindowEnds);
        update = row.ProviderRefundId is null ? update.Unset(lot => lot.ProviderRefundId) : update.Set(lot => lot.ProviderRefundId, row.ProviderRefundId);
        update = row.ProviderRefundStatus is null ? update.Unset(lot => lot.ProviderRefundStatus) : update.Set(lot => lot.ProviderRefundStatus, row.ProviderRefundStatus);
        update = row.RefundStartedAt is null ? update.Unset(lot => lot.RefundStartedAt) : update.Set(lot => lot.RefundStartedAt, row.RefundStartedAt);
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var result = await executor.ExecuteAsync(update.Build(), ct);
            result.EnsureAllOk();
            return result;
        }, ct);
        var saved = response.GetValues<OnRampLot>(0).SingleOrDefault();
        return saved is null ? AZOAResult<OnRampLot>.Failure("The lot changed during this update.")
            : AZOAResult<OnRampLot>.Success(saved);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListIssuedForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var subject = SurrealLink.ToLink(User.SchemaNameConst, userId.ToString("N"));
        var rows = await executor.QueryAsync<OnRampLot>(SurrealQuery<OnRampLot>.From()
            .Where(row => row.SubjectUserId == subject && row.Status == OnRampLotStatuses.Issued)
            .OrderBy(row => row.IssuedAt!), ct);
        return AZOAResult<IReadOnlyList<OnRampLot>>.Success(rows.ToList());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListNeedingOperatorAsync(int limit, CancellationToken ct = default)
    {
        var capped = Math.Clamp(limit, 1, 500);
        var before = DateTimeOffset.UtcNow - StrandedPendingAfter;
        var decided = await executor.QueryAsync<OnRampLot>(SurrealQuery<OnRampLot>.From()
            .Where(row => row.Status == OnRampLotStatuses.Refused || row.Status == OnRampLotStatuses.Stuck
                || row.Status == OnRampLotStatuses.RefundPending).OrderByDescending(row => row.CreatedAt).Limit(capped), ct);
        var stranded = await executor.QueryAsync<OnRampLot>(SurrealQuery<OnRampLot>.From()
            .Where(row => row.Status == OnRampLotStatuses.Pending && row.CreatedAt < before)
            .OrderByDescending(row => row.CreatedAt).Limit(capped), ct);
        return AZOAResult<IReadOnlyList<OnRampLot>>.Success(MergeOperatorReads(decided, stranded, capped));
    }

    public static IReadOnlyList<OnRampLot> MergeOperatorReads(IEnumerable<OnRampLot> decided,
        IEnumerable<OnRampLot> strandedPending, int limit)
        => decided.Concat(strandedPending).GroupBy(row => row.Id, StringComparer.Ordinal)
            .Select(group => group.First()).OrderByDescending(row => row.CreatedAt).Take(Math.Max(limit, 1)).ToList();
}
