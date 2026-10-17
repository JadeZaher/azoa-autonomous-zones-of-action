// SPDX-License-Identifier: UNLICENSED


using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;   // SurrealTransientConflict (retryable 3.x txn conflicts)
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>Durable states of one cash-out. Mirrors the POCO allow-list.</summary>
public static class FiatPayoutStatuses
{
    public const string BankPending = "BankPending";
    /// <summary>Claimed. Nothing has moved and nothing has been promised to the provider.</summary>
    public const string Requested = "Requested";

    /// <summary>
    /// The units are back in the reserve and the money has NOT been sent. The only state where a
    /// person is out of units and has no money, which is why nothing may leave it silently.
    /// </summary>
    public const string UnitsCollected = "UnitsCollected";

    /// <summary>The bank payout is confirmed paid by the provider.</summary>
    public const string Paid = "Paid";

    /// <summary>The provider pulled the money back out again (W4.3 records the fact).</summary>
    public const string Reversed = "Reversed";

    /// <summary>The cash-out was abandoned and the units were given back. Terminal, benign.</summary>
    public const string Returned = "Returned";

    /// <summary>Refused before anything moved. Terminal, benign.</summary>
    public const string Refused = "Refused";

    /// <summary>Only a person can move this on; the reason is always on the row.</summary>
    public const string NeedsOperator = "NeedsOperator";

    /// <summary>Statuses whose units are promised but not yet gone from the person's account.</summary>
    public static readonly IReadOnlyList<string> Committing = new[] { Requested, UnitsCollected };

    /// <summary>Statuses an operator board must show: in flight, or waiting on a person.</summary>
    public static readonly IReadOnlyList<string> Board = new[]
    {
        Requested, UnitsCollected, BankPending, Reversed, NeedsOperator,
    };

    /// <summary>True for a status no further automatic step will change.</summary>
    public static bool IsSettled(string? status) =>
        status is Paid or Returned or Refused;

    /// <summary>True for a status that is waiting on a person rather than on the saga.</summary>
    public static bool NeedsPerson(string? status) =>
        string.Equals(status, NeedsOperator, StringComparison.Ordinal)
        || string.Equals(status, Reversed, StringComparison.Ordinal);
}

/// <summary>Outcome of a create-if-absent claim: the row, plus whether this caller created it.</summary>
public sealed record FiatPayoutClaim(FiatPayout Row, bool Created);

/// <summary>
/// The durable record of every cash-out this node has been asked for. See
/// <c>Services/Fiat/OffRamp/AGENTS.md</c>.
/// </summary>
public interface IFiatPayoutLedger
{
    /// <summary>Creates the cash-out only if it is absent. A losing racer gets the winner's row.</summary>
    Task<AZOAResult<FiatPayoutClaim>> ClaimAsync(FiatPayout candidate, CancellationToken ct = default);

    /// <summary>One cash-out by record id; a null result when there is none.</summary>
    Task<AZOAResult<FiatPayout?>> GetAsync(string recordId, CancellationToken ct = default);

    /// <summary>The cash-out a provider transfer belongs to, or a null result when there is none.</summary>
    Task<AZOAResult<FiatPayout?>> GetByTransferAsync(string transferId, CancellationToken ct = default);

    /// <summary>
    /// Compare-and-swap write: lands only while the persisted <c>version</c> is still
    /// <paramref name="expectedVersion"/>. A lost race is an error, never a silent overwrite.
    /// </summary>
    Task<AZOAResult<FiatPayout>> UpdateIfVersionAsync(
        FiatPayout row, long expectedVersion, CancellationToken ct = default);

    /// <summary>One person's cash-outs, newest first. What they see on their own history.</summary>
    Task<AZOAResult<IReadOnlyList<FiatPayout>>> ListForUserAsync(
        Guid userId, int limit, CancellationToken ct = default);

    /// <summary>
    /// One person's cash-outs whose units are promised but not yet gone. Subtracted from what
    /// they may cash out, so two requests in flight cannot both spend the same units.
    /// </summary>
    Task<AZOAResult<IReadOnlyList<FiatPayout>>> ListCommittingForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>The operator board: in flight, reversed, or waiting on a person. Newest first.</summary>
    Task<AZOAResult<IReadOnlyList<FiatPayout>>> ListBoardAsync(int limit, CancellationToken ct = default);
}

/// <summary>
/// SurrealDB-backed payout ledger. Colocated with the saga because
/// <c>Providers/Stores/Surreal/</c> is outside this item's file boundary; the interface is
/// <see cref="IFiatPayoutLedger"/>, so moving it later is a file move.
/// </summary>
public sealed class SurrealFiatPayoutLedger(ISurrealExecutor executor) : IFiatPayoutLedger
{
    /// <inheritdoc />
    public async Task<AZOAResult<FiatPayoutClaim>> ClaimAsync(FiatPayout candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        try
        {
            var response = await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct);
            response.EnsureAllOk();
            var row = response.GetValues<FiatPayout>(0).Single();
            return AZOAResult<FiatPayoutClaim>.Success(new(row, true));
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var existing = await GetAsync(candidate.Id, ct);
            if (existing.Result is null) throw;
            return AZOAResult<FiatPayoutClaim>.Success(new(existing.Result, false));
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatPayout?>> GetAsync(string recordId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordId)) return new() { Result = null };
        var row = await executor.QuerySingleAsync<FiatPayout>(SurrealQuery<FiatPayout>.Key(recordId), ct);
        return new() { Result = row };
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatPayout?>> GetByTransferAsync(string transferId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transferId)) return new() { Result = null };
        var row = await executor.QuerySingleAsync<FiatPayout>(SurrealQuery<FiatPayout>.From()
            .Where(payout => payout.ProviderTransferId == transferId).OrderByDescending(payout => payout.RequestedAt).Limit(1), ct);
        return new() { Result = row };
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatPayout>> UpdateIfVersionAsync(FiatPayout row, long expectedVersion, CancellationToken ct = default)
    {
        var update = SurrealWriter.UpdateOnly<FiatPayout>(row.Id)
            .Where(payout => payout.Version == expectedVersion)
            .Set(payout => payout.AgedQuantityBaseUnits, row.AgedQuantityBaseUnits)
            .Set(payout => payout.BankPayoutAttempt, row.BankPayoutAttempt)
            .Set(payout => payout.Status, row.Status)
            .Set(payout => payout.UnitsOwedBaseUnits, row.UnitsOwedBaseUnits)
            .Set(payout => payout.ReversedMinorUnits, row.ReversedMinorUnits)
            .Set(payout => payout.AttemptCount, row.AttemptCount)
            .Set(payout => payout.Version, row.Version)
            .Set(payout => payout.UpdatedAt, row.UpdatedAt);
        update = update.Set(payout => payout.RecoveryRequested, row.RecoveryRequested)
            .Set(payout => payout.RecoveryReversalAmount, row.RecoveryReversalAmount)
            .Set(payout => payout.RecoveryReversalAttempt, row.RecoveryReversalAttempt)
            .Set(payout => payout.RestoredQuantityBaseUnits, row.RestoredQuantityBaseUnits)
            .Set(payout => payout.RestitutionTargetBaseUnits, row.RestitutionTargetBaseUnits)
            .Set(payout => payout.RecoveryAuditJson, row.RecoveryAuditJson);
        update = row.RecoveryStartedAt is null ? update.Unset(payout => payout.RecoveryStartedAt) : update.Set(payout => payout.RecoveryStartedAt, row.RecoveryStartedAt);
        update = row.ProviderTransferReversalId is null ? update.Unset(payout => payout.ProviderTransferReversalId) : update.Set(payout => payout.ProviderTransferReversalId, row.ProviderTransferReversalId);
        update = row.ReturnPendingTransactionId is null ? update.Unset(payout => payout.ReturnPendingTransactionId) : update.Set(payout => payout.ReturnPendingTransactionId, row.ReturnPendingTransactionId);
        update = row.ReturnPendingIdempotencyKey is null ? update.Unset(payout => payout.ReturnPendingIdempotencyKey) : update.Set(payout => payout.ReturnPendingIdempotencyKey, row.ReturnPendingIdempotencyKey);
        update = row.RestitutionKey is null ? update.Unset(payout => payout.RestitutionKey) : update.Set(payout => payout.RestitutionKey, row.RestitutionKey);
        update = row.ConsumedLots is null ? update.Unset(payout => payout.ConsumedLots) : update.Set(payout => payout.ConsumedLots, row.ConsumedLots);
        update = row.ConnectedAccountId is null ? update.Unset(payout => payout.ConnectedAccountId) : update.Set(payout => payout.ConnectedAccountId, row.ConnectedAccountId);
        update = row.ProviderTransferId is null ? update.Unset(payout => payout.ProviderTransferId) : update.Set(payout => payout.ProviderTransferId, row.ProviderTransferId);
        update = row.ProviderPayoutId is null ? update.Unset(payout => payout.ProviderPayoutId) : update.Set(payout => payout.ProviderPayoutId, row.ProviderPayoutId);
        update = row.BankPayoutStartedAt is null ? update.Unset(payout => payout.BankPayoutStartedAt) : update.Set(payout => payout.BankPayoutStartedAt, row.BankPayoutStartedAt);
        update = row.UnitsCollectedTransactionId is null ? update.Unset(payout => payout.UnitsCollectedTransactionId) : update.Set(payout => payout.UnitsCollectedTransactionId, row.UnitsCollectedTransactionId);
        update = row.UnitsReturnedTransactionId is null ? update.Unset(payout => payout.UnitsReturnedTransactionId) : update.Set(payout => payout.UnitsReturnedTransactionId, row.UnitsReturnedTransactionId);
        update = row.StatusReason is null ? update.Unset(payout => payout.StatusReason) : update.Set(payout => payout.StatusReason, row.StatusReason);
        update = row.ReversalId is null ? update.Unset(payout => payout.ReversalId) : update.Set(payout => payout.ReversalId, row.ReversalId);
        update = row.UnitsCollectedAt is null ? update.Unset(payout => payout.UnitsCollectedAt) : update.Set(payout => payout.UnitsCollectedAt, row.UnitsCollectedAt);
        update = row.PaidAt is null ? update.Unset(payout => payout.PaidAt) : update.Set(payout => payout.PaidAt, row.PaidAt);
        update = row.ReversedAt is null ? update.Unset(payout => payout.ReversedAt) : update.Set(payout => payout.ReversedAt, row.ReversedAt);
        // funding_tenant_id is immutable after the claim (D-8: who pays = who priced, fixed at claim time).
        update = row.CollectPartition is null ? update.Unset(payout => payout.CollectPartition) : update.Set(payout => payout.CollectPartition, row.CollectPartition);
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var result = await executor.ExecuteAsync(update.Build(), ct);
            result.EnsureAllOk();
            return result;
        }, ct);
        var saved = response.GetValues<FiatPayout>(0).SingleOrDefault();
        return saved is null ? AZOAResult<FiatPayout>.Failure("Cash-out changed during this update.")
            : AZOAResult<FiatPayout>.Success(saved);
    }

    /// <inheritdoc />
    public Task<AZOAResult<IReadOnlyList<FiatPayout>>> ListForUserAsync(Guid userId, int limit, CancellationToken ct = default)
    {
        var subject = SurrealLink.ToLink(User.SchemaNameConst, userId.ToString("N"));
        return ReadAsync(SurrealQuery<FiatPayout>.From().Where(row => row.SubjectUserId == subject)
            .OrderByDescending(row => row.RequestedAt).Limit(Math.Clamp(limit, 1, 500)).AsUntyped(), ct);
    }

    /// <inheritdoc />
    public Task<AZOAResult<IReadOnlyList<FiatPayout>>> ListCommittingForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var subject = SurrealLink.ToLink(User.SchemaNameConst, userId.ToString("N"));
        return ReadAsync(SurrealQuery<FiatPayout>.From().Where(row => row.SubjectUserId == subject
            && (row.Status == FiatPayoutStatuses.Requested || row.Status == FiatPayoutStatuses.UnitsCollected))
            .OrderBy(row => row.RequestedAt).AsUntyped(), ct);
    }

    /// <inheritdoc />
    public Task<AZOAResult<IReadOnlyList<FiatPayout>>> ListBoardAsync(int limit, CancellationToken ct = default)
        => ReadAsync(SurrealQuery<FiatPayout>.From().Where(row => row.Status == FiatPayoutStatuses.Requested
            || row.Status == FiatPayoutStatuses.UnitsCollected || row.Status == FiatPayoutStatuses.BankPending
            || row.Status == FiatPayoutStatuses.Reversed || row.Status == FiatPayoutStatuses.NeedsOperator)
            .OrderByDescending(row => row.RequestedAt).Limit(Math.Clamp(limit, 1, 500)).AsUntyped(), ct);

    private async Task<AZOAResult<IReadOnlyList<FiatPayout>>> ReadAsync(SurrealQuery query, CancellationToken ct)
        => AZOAResult<IReadOnlyList<FiatPayout>>.Success((await executor.QueryAsync<FiatPayout>(query, ct)).ToList());
}
