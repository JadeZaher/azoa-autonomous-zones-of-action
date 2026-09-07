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
        CreatedDate = claim.CreatedDate,
        ModifiedDate = claim.ModifiedDate,
    };
}
