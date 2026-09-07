// SPDX-License-Identifier: UNLICENSED

using System.Diagnostics;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IPersonOperationClaimStore"/>.
///
/// <para>Every statement names <c>person_operation_claim</c> from a compile-time
/// constant. No caller-supplied table name reaches this store, and the table is
/// absent from every generic read surface, so the per-operation claim ledger has
/// no consumer-reachable query path at all.</para>
/// </summary>
public sealed class SurrealPersonOperationClaimStore : IPersonOperationClaimStore
{
    private const string PublicStoreError =
        "PERSON_OPERATION_CLAIM_STORE_UNAVAILABLE: Claim persistence is temporarily unavailable.";

    private readonly ISurrealExecutor _executor;
    private readonly ILogger<SurrealPersonOperationClaimStore> _logger;

    public SurrealPersonOperationClaimStore(
        ISurrealExecutor executor,
        ILogger<SurrealPersonOperationClaimStore>? logger = null)
    {
        _executor = executor;
        _logger = logger ?? NullLogger<SurrealPersonOperationClaimStore>.Instance;
    }

    public async Task<AZOAResult<PersonOperationClaim>> TryClaimAsync(
        PersonOperationClaim candidate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(candidate.UserId))
            return AZOAResult<PersonOperationClaim>.Failure("A user is required to claim an operation.");

        try
        {
            var body = ToStorage(candidate);
            var now = body.ModifiedDate ?? DateTimeOffset.UtcNow;

            // Single-winner claim, decided INSIDE the statement on the deterministic
            // record id. The caller learns whether it won by reading user_id back and
            // comparing it to its own; a preceding "is it free?" read would be passed
            // by BOTH concurrent claimants and would make the whole gate a tautology
            // (see Services/Kyc/AGENTS.md, 'The Sybil refusal lives at the WRITE').
            //
            // ORDERING IS LOAD-BEARING. SurrealDB applies SET assignments in sequence,
            // so a later expression sees an earlier assignment. Every conditional here
            // reads the guard G = (user_id is unset / ours / released), and G is
            // assigned to LAST via user_id and then released_at. That order is safe
            // because setting user_id = $_user_id can only turn G true, and only ever
            // does so on a branch where G was already true — so G cannot flip under
            // its own writes. Reversing the order silently hands a released claim's
            // row to the wrong holder.
            //
            // D39/D40 adds the DECISION to the same statement, before user_id for the
            // same reason. Its extra term is the voting window, evaluated against the
            // close time PINNED ON THE ROW by the first decision and only falling back
            // to the caller's when the row has none — so a proposal whose closesAt is
            // rewritten afterwards cannot reopen a decision that is already frozen.
            // decision_closes_at is likewise write-once. A refused decision leaves the
            // stored one untouched, which is what "frozen at close" has to mean.
            // `decision_closes_at ?? $_decision_closes_at` is therefore always a real
            // datetime: the row's pinned close once one exists, the caller's otherwise.
            var query = SurrealQuery
                .Of("""
                    UPSERT type::record($_t, $_id)
                    SET uniqueness_scope = $_scope,
                        operation_key = $_operation_key,
                        person_key = $_person_key,
                        claimed_at = IF (user_id = NONE
                                OR user_id = ''
                                OR released_at != NONE)
                            THEN $_now ELSE claimed_at END,
                        tenant_id = IF (user_id = NONE
                                OR user_id = ''
                                OR user_id = $_user_id
                                OR released_at != NONE)
                            THEN $_tenant ELSE tenant_id END,
                        modified_date = $_now,
                        decision_option = IF ($_decision != ''
                                AND (user_id = NONE
                                    OR user_id = ''
                                    OR user_id = $_user_id
                                    OR released_at != NONE)
                                AND $_now < (decision_closes_at ?? $_decision_closes_at))
                            THEN $_decision ELSE decision_option END,
                        decided_at = IF ($_decision != ''
                                AND (user_id = NONE
                                    OR user_id = ''
                                    OR user_id = $_user_id
                                    OR released_at != NONE)
                                AND $_now < (decision_closes_at ?? $_decision_closes_at))
                            THEN $_now ELSE decided_at END,
                        decision_closes_at = IF ($_decision != ''
                                AND decision_closes_at = NONE
                                AND (user_id = NONE
                                    OR user_id = ''
                                    OR user_id = $_user_id
                                    OR released_at != NONE)
                                AND $_now < $_decision_closes_at)
                            THEN $_decision_closes_at ELSE decision_closes_at END,
                        user_id = IF (user_id = NONE
                                OR user_id = ''
                                OR user_id = $_user_id
                                OR released_at != NONE)
                            THEN $_user_id ELSE user_id END,
                        released_at = IF (user_id = NONE
                                OR user_id = ''
                                OR user_id = $_user_id
                                OR released_at != NONE)
                            THEN NONE ELSE released_at END
                    RETURN AFTER
                    """)
                .WithParam("_t", PersonOperationClaim.SchemaNameConst)
                .WithParam("_id", body.Id)
                .WithParam("_scope", body.UniquenessScope)
                .WithParam("_operation_key", body.OperationKey)
                .WithParam("_person_key", body.PersonKey)
                .WithParam("_user_id", body.UserId)
                .WithParam("_tenant", SurrealRecordParam.Of("user", candidate.TenantId))
                // NEITHER decision param is ever null. A JSON null is SurrealDB NULL,
                // not NONE, and NULL propagates through `??` and through every
                // comparison in the guards above — which would decide the window the
                // wrong way in silence. So "no decision" is the empty string and
                // "never closes" is PersonOperationClaim.NeverClosesAt, and the guards
                // test those two definite values instead of testing for absence.
                .WithParam("_decision", body.DecisionOption ?? string.Empty)
                .WithParam("_decision_closes_at", body.DecisionClosesAt ?? PersonOperationClaim.NeverClosesAt)
                .WithParam("_now", now);

            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(query, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);

            var saved = response.GetValues<PersonOperationClaim>(0).FirstOrDefault();
            return AZOAResult<PersonOperationClaim>.Success(
                saved is null ? candidate : FromStorage(saved),
                "Saved.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<PersonOperationClaim>(exception, nameof(TryClaimAsync), candidate.Id);
        }
    }

    public async Task<AZOAResult<PersonOperationClaim?>> GetAsync(
        string uniquenessScope,
        string operationKey,
        string personKey,
        CancellationToken ct = default)
    {
        try
        {
            var recordId = PersonOperationClaim.RecordIdFor(uniquenessScope, operationKey, personKey);
            var query = SurrealQuery.SelectById(PersonOperationClaim.SchemaNameConst, recordId);

            var row = await _executor.QuerySingleAsync<PersonOperationClaim>(query, ct);
            return new AZOAResult<PersonOperationClaim?>
            {
                Message = row is null ? "No claim found." : "Success",
                Result = row is null ? null : FromStorage(row),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<PersonOperationClaim?>(exception, nameof(GetAsync), uniquenessScope);
        }
    }

    public async Task<AZOAResult<IReadOnlyList<PersonOperationClaim>>> ListLiveByOperationAsync(
        string uniquenessScope,
        string operationKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uniquenessScope) || string.IsNullOrWhiteSpace(operationKey))
            return AZOAResult<IReadOnlyList<PersonOperationClaim>>.Failure(
                "A uniqueness scope and an operation key are required to list claims.");

        try
        {
            // PROJECTION, not SELECT *. The tally needs the decision, the holder and
            // the liveness flag; it has no business holding a person key in process
            // memory, and a projection is the only way to make that true rather than
            // merely intended. person_key, id and operation_key are deliberately
            // absent from the returned rows.
            var query = SurrealQuery
                .Of("""
                    SELECT user_id, released_at, decision_option, decided_at, decision_closes_at
                    FROM type::table($_t)
                    WHERE uniqueness_scope = $_scope
                      AND operation_key = $_operation_key
                      AND released_at = NONE
                    """)
                .WithParam("_t", PersonOperationClaim.SchemaNameConst)
                .WithParam("_scope", uniquenessScope)
                .WithParam("_operation_key", operationKey);

            var rows = await _executor.QueryAsync<PersonOperationClaim>(query, ct);

            // No LIMIT on purpose: a truncated electorate publishes a wrong count
            // under a correct-looking tally hash, which is worse than a slow read.
            return AZOAResult<IReadOnlyList<PersonOperationClaim>>.Success(rows, "Success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<IReadOnlyList<PersonOperationClaim>>(
                exception, nameof(ListLiveByOperationAsync), uniquenessScope);
        }
    }

    public async Task<AZOAResult<bool>> ReleaseAsync(
        string uniquenessScope,
        string operationKey,
        string personKey,
        string userId,
        DateTimeOffset releasedAt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return AZOAResult<bool>.Success(false, "Nothing to release.");

        try
        {
            var recordId = PersonOperationClaim.RecordIdFor(uniquenessScope, operationKey, personKey);
            var stamp = releasedAt.ToUniversalTime();

            // Conditional UPDATE, so a replayed compensation and a release by a user
            // who does not hold the claim both resolve to "nothing was held" instead
            // of stealing the row from its real holder.
            var query = SurrealQuery
                .Of("""
                    UPDATE type::record($_t, $_id)
                    SET released_at = $_released_at,
                        modified_date = $_released_at
                    WHERE user_id = $_user_id AND released_at = NONE
                    RETURN AFTER
                    """)
                .WithParam("_t", PersonOperationClaim.SchemaNameConst)
                .WithParam("_id", recordId)
                .WithParam("_user_id", userId)
                .WithParam("_released_at", stamp);

            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(query, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);

            var released = response.GetValues<PersonOperationClaim>(0).FirstOrDefault();
            return AZOAResult<bool>.Success(released is not null, released is null ? "Nothing to release." : "Released.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<bool>(exception, nameof(ReleaseAsync), uniquenessScope);
        }
    }

    private AZOAResult<T> StoreFailure<T>(Exception exception, string operation, object entityId)
    {
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        // entityId is a scope or an opaque digest, never a person key, so it is safe
        // to correlate on.
        _logger.LogError(
            exception,
            "Person operation claim persistence failure in {Operation}; correlation={CorrelationId}; entity={EntityId}",
            operation,
            correlationId,
            entityId);
        return AZOAResult<T>.FailureWithCode(PublicStoreError, AzoaErrorCodes.DependencyUnavailable);
    }

    private static PersonOperationClaim ToStorage(PersonOperationClaim claim) => new()
    {
        Id = claim.Id,
        UniquenessScope = claim.UniquenessScope,
        OperationKey = claim.OperationKey,
        PersonKey = claim.PersonKey,
        UserId = claim.UserId,
        TenantId = string.IsNullOrWhiteSpace(claim.TenantId)
            ? null
            : SurrealLink.ToLink("user", claim.TenantId),
        ClaimedAt = claim.ClaimedAt?.ToUniversalTime(),
        ReleasedAt = claim.ReleasedAt?.ToUniversalTime(),
        DecisionOption = claim.DecisionOption,
        DecidedAt = claim.DecidedAt?.ToUniversalTime(),
        DecisionClosesAt = claim.DecisionClosesAt?.ToUniversalTime(),
        CreatedDate = claim.CreatedDate.ToUniversalTime(),
        ModifiedDate = claim.ModifiedDate?.ToUniversalTime(),
    };

    private static PersonOperationClaim FromStorage(PersonOperationClaim claim) => new()
    {
        Id = claim.Id,
        UniquenessScope = claim.UniquenessScope,
        OperationKey = claim.OperationKey,
        PersonKey = claim.PersonKey,
        UserId = claim.UserId,
        TenantId = string.IsNullOrWhiteSpace(claim.TenantId)
            ? null
            : SurrealLink.FromLink(claim.TenantId),
        ClaimedAt = claim.ClaimedAt,
        ReleasedAt = claim.ReleasedAt,
        DecisionOption = claim.DecisionOption,
        DecidedAt = claim.DecidedAt,
        // The never-closes sentinel is a storage detail; the domain says "no close".
        DecisionClosesAt = PersonOperationClaim.IsNeverCloses(claim.DecisionClosesAt)
            ? null
            : claim.DecisionClosesAt,
        CreatedDate = claim.CreatedDate,
        ModifiedDate = claim.ModifiedDate,
    };
}
