// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core.Surreal;   // SurrealRecordParam (record<> binding, never a bare hex id)
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;   // SurrealTransientConflict (retryable 3.x txn conflicts)
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>Durable states of one cash-out. Mirrors the POCO allow-list.</summary>
public static class FiatPayoutStatuses
{
    /// <summary>Claimed. Nothing has moved and nothing has been promised to the provider.</summary>
    public const string Requested = "Requested";

    /// <summary>
    /// The units are back in the reserve and the money has NOT been sent. The only state where a
    /// person is out of units and has no money, which is why nothing may leave it silently.
    /// </summary>
    public const string UnitsCollected = "UnitsCollected";

    /// <summary>The transfer exists at the provider. Terminal unless it is reversed.</summary>
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
        Requested, UnitsCollected, Reversed, NeedsOperator,
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
public sealed class SurrealFiatPayoutLedger : IFiatPayoutLedger
{
    private const string Table = FiatPayout.SchemaNameConst;

    private const string ForUserStatement =
        "SELECT * FROM fiat_payout WHERE subject_user_id = $_user ORDER BY requested_at DESC LIMIT $_limit";

    private const string CommittingForUserStatement =
        "SELECT * FROM fiat_payout WHERE subject_user_id = $_user AND status INSIDE $_statuses "
        + "ORDER BY requested_at ASC";

    private const string BoardStatement =
        "SELECT * FROM fiat_payout WHERE status INSIDE $_statuses ORDER BY requested_at DESC LIMIT $_limit";

    private const string ByTransferStatement =
        "SELECT * FROM fiat_payout WHERE provider_transfer_id = $_transfer ORDER BY requested_at DESC LIMIT 1";

    private readonly ISurrealExecutor _executor;

    public SurrealFiatPayoutLedger(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatPayoutClaim>> ClaimAsync(
        FiatPayout candidate, CancellationToken ct = default)
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

            var created = response.GetValues<FiatPayout>(0).SingleOrDefault() ?? candidate;
            return AZOAResult<FiatPayoutClaim>.Success(new FiatPayoutClaim(created, Created: true), "Created.");
        }
        catch (Exception ex)
        {
            // A CREATE that lost to an existing row is the double-tap case, not a fault — but only
            // when the row really is there now. Re-read before concluding that, so a transport
            // failure is never reported as "this cash-out already exists" and answered with a
            // stale row nobody is driving.
            var existing = await GetAsync(candidate.Id, ct);
            if (!existing.IsError && existing.Result is not null)
                return AZOAResult<FiatPayoutClaim>.Success(
                    new FiatPayoutClaim(existing.Result, Created: false), "Already claimed.");

            return new AZOAResult<FiatPayoutClaim>()
                .CaptureException(ex, $"Cash-out write failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatPayout?>> GetAsync(string recordId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordId))
            return new AZOAResult<FiatPayout?> { Result = null, Message = "Not found." };

        try
        {
            var query = SurrealQuery<FiatPayout>.Key(recordId.Trim());
            var row = await _executor.QuerySingleAsync<FiatPayout>(query, ct);
            return new AZOAResult<FiatPayout?> { Result = row, Message = row is null ? "Not found." : "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<FiatPayout?>()
                .CaptureException(ex, $"Cash-out read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatPayout?>> GetByTransferAsync(
        string transferId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transferId))
            return new AZOAResult<FiatPayout?> { Result = null, Message = "Not found." };

        try
        {
            var query = SurrealQuery.Of(ByTransferStatement).WithParam("_transfer", transferId.Trim());
            var rows = await _executor.QueryAsync<FiatPayout>(query, ct);
            var row = rows?.FirstOrDefault();
            return new AZOAResult<FiatPayout?> { Result = row, Message = row is null ? "Not found." : "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<FiatPayout?>()
                .CaptureException(ex, $"Cash-out read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatPayout>> UpdateIfVersionAsync(
        FiatPayout row, long expectedVersion, CancellationToken ct = default)
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
                return AZOAResult<FiatPayout>.Failure(
                    $"Cash-out '{row.Id}' changed underneath this update "
                    + $"(expected version {expectedVersion}).");

            return AZOAResult<FiatPayout>.Success(
                response.GetValues<FiatPayout>(0).SingleOrDefault() ?? row, "Saved.");
        }
        catch (Exception ex)
        {
            return new AZOAResult<FiatPayout>()
                .CaptureException(ex, $"Cash-out update failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<FiatPayout>>> ListForUserAsync(
        Guid userId, int limit, CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
            return AZOAResult<IReadOnlyList<FiatPayout>>.Success(Array.Empty<FiatPayout>(), "Success");

        try
        {
            // subject_user_id is a record<user> column, so the parameter has to be bound as a
            // native record id — a bare string compares equal to nothing and would answer "this
            // person has never cashed out" with no error at all.
            var query = SurrealQuery
                .Of(ForUserStatement)
                .WithParam("_user", SurrealRecordParam.Of(User.SchemaNameConst, userId.ToString("N")))
                .WithParam("_limit", Math.Clamp(limit, 1, 500));

            return await ReadAsync(query, ct);
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<FiatPayout>>()
                .CaptureException(ex, $"Cash-out read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<FiatPayout>>> ListCommittingForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
            return AZOAResult<IReadOnlyList<FiatPayout>>.Success(Array.Empty<FiatPayout>(), "Success");

        try
        {
            var query = SurrealQuery
                .Of(CommittingForUserStatement)
                .WithParam("_user", SurrealRecordParam.Of(User.SchemaNameConst, userId.ToString("N")))
                .WithParam("_statuses", FiatPayoutStatuses.Committing.ToArray());

            return await ReadAsync(query, ct);
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<FiatPayout>>()
                .CaptureException(ex, $"Cash-out read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<FiatPayout>>> ListBoardAsync(
        int limit, CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of(BoardStatement)
                .WithParam("_statuses", FiatPayoutStatuses.Board.ToArray())
                .WithParam("_limit", Math.Clamp(limit, 1, 500));

            return await ReadAsync(query, ct);
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<FiatPayout>>()
                .CaptureException(ex, $"Cash-out read failed: {ex.Message}");
        }
    }

    private async Task<AZOAResult<IReadOnlyList<FiatPayout>>> ReadAsync(
        SurrealQuery query, CancellationToken ct)
    {
        var rows = await _executor.QueryAsync<FiatPayout>(query, ct);
        return AZOAResult<IReadOnlyList<FiatPayout>>.Success(
            rows?.ToList() ?? new List<FiatPayout>(), "Success");
    }
}
