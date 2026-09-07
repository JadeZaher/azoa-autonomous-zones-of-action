// SPDX-License-Identifier: UNLICENSED

using System.Diagnostics;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IKycIdentityStore"/>.
///
/// <para>Every statement here names <c>kyc_identity</c> from a compile-time
/// constant. No caller-supplied table name reaches this store, and the table is
/// absent from every generic read surface, so the person-to-user link has no
/// consumer-reachable query path at all.</para>
///
/// <para>ID encoding mirrors <see cref="SurrealKycStore"/>: bare hex in memory,
/// <c>table:id</c> record links on the wire.</para>
/// </summary>
public sealed class SurrealKycIdentityStore : IKycIdentityStore
{
    private const string PublicStoreError =
        "KYC_IDENTITY_STORE_UNAVAILABLE: Identity persistence is temporarily unavailable.";

    private readonly ISurrealExecutor _executor;
    private readonly ILogger<SurrealKycIdentityStore> _logger;

    public SurrealKycIdentityStore(
        ISurrealExecutor executor,
        ILogger<SurrealKycIdentityStore>? logger = null)
    {
        _executor = executor;
        _logger = logger ?? NullLogger<SurrealKycIdentityStore>.Instance;
    }

    public async Task<AZOAResult<KycIdentity>> BindUserAsync(
        KycIdentity candidate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        try
        {
            var body = ToStorage(candidate);
            var newUser = candidate.UserIds?.FirstOrDefault();

            // Create-or-append in one statement. An existing binding keeps its
            // original keys, provenance, and creation time; only the user set and
            // the modified stamp move.
            //
            // The append is CONDITIONAL, not unconditional. An unconditional
            // array::concat adds the caller to every existing binding, which makes
            // "this person is already bound to a different user" impossible to
            // observe afterwards and turns the D3/D23 Sybil check into a tautology.
            // The condition is expressed in the statement rather than in a preceding
            // read so two concurrent binds for one person key cannot both win.
            var query = SurrealQuery
                .Of("""
                    UPSERT type::record($_t, $_id)
                    SET tenant_scope = $_scope,
                        tenant_id = $_tenant,
                        person_key = $_person_key,
                        global_person_key = $_global_person_key,
                        person_key_version = $_key_version,
                        user_ids = IF (user_ids = NONE
                                OR array::len(user_ids) = 0
                                OR $_new_user INSIDE user_ids)
                            THEN array::distinct(array::concat(user_ids OR [], $_new_users))
                            ELSE user_ids END,
                        first_name = $_first_name,
                        last_name = $_last_name,
                        birthday = $_birthday,
                        citizenship = $_citizenship,
                        provider = $_provider,
                        provider_key = $_provider_key,
                        retention_expires_at = $_retention_expires_at,
                        modified_date = $_now
                    RETURN AFTER
                    """)
                .WithParam("_t", KycIdentity.SchemaNameConst)
                .WithParam("_id", body.Id)
                .WithParam("_scope", body.TenantScope)
                .WithParam("_tenant", SurrealRecordParam.Of("user", candidate.TenantId))
                .WithParam("_person_key", body.PersonKey)
                .WithParam("_global_person_key", body.GlobalPersonKey)
                .WithParam("_key_version", body.PersonKeyVersion)
                .WithParam("_new_user", newUser)
                .WithParam("_new_users", string.IsNullOrWhiteSpace(newUser)
                    ? Array.Empty<string>()
                    : new[] { newUser! })
                .WithParam("_first_name", body.FirstName)
                .WithParam("_last_name", body.LastName)
                .WithParam("_birthday", body.Birthday)
                .WithParam("_citizenship", body.Citizenship)
                .WithParam("_provider", body.Provider.ToString())
                .WithParam("_provider_key", body.ProviderKey)
                .WithParam("_retention_expires_at", body.RetentionExpiresAt)
                .WithParam("_now", body.ModifiedDate ?? DateTimeOffset.UtcNow);

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();

            var saved = response.GetValues<KycIdentity>(0).FirstOrDefault();
            return AZOAResult<KycIdentity>.Success(
                saved is null ? candidate : FromStorage(saved),
                "Saved.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<KycIdentity>(exception, nameof(BindUserAsync), candidate.Id);
        }
    }

    public async Task<AZOAResult<KycIdentity>> GetByPersonKeyAsync(
        string tenantScope,
        string personKey,
        CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE tenant_scope = $_scope AND person_key = $_person_key LIMIT 1")
                .WithParam("_t", KycIdentity.SchemaNameConst)
                .WithParam("_scope", tenantScope)
                .WithParam("_person_key", personKey);

            var row = await _executor.QuerySingleAsync<KycIdentity>(query, ct);
            return new AZOAResult<KycIdentity>
            {
                Message = row is null ? "No identity binding found." : "Success",
                Result = row is null ? null : FromStorage(row),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<KycIdentity>(exception, nameof(GetByPersonKeyAsync), tenantScope);
        }
    }

    public async Task<AZOAResult<KycIdentity?>> GetByUserAsync(
        string tenantScope,
        string userId,
        CancellationToken ct = default)
    {
        try
        {
            // user_ids is an array; SurrealDB indexes array fields elementwise, so
            // kyc_identity_scope_users serves this INSIDE predicate directly.
            var query = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE tenant_scope = $_scope AND $_user_id INSIDE user_ids LIMIT 1")
                .WithParam("_t", KycIdentity.SchemaNameConst)
                .WithParam("_scope", tenantScope)
                .WithParam("_user_id", userId);

            var row = await _executor.QuerySingleAsync<KycIdentity>(query, ct);
            return new AZOAResult<KycIdentity?>
            {
                Message = row is null ? "No identity binding found." : "Success",
                Result = row is null ? null : FromStorage(row),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<KycIdentity?>(exception, nameof(GetByUserAsync), tenantScope);
        }
    }

    public async Task<AZOAResult<IReadOnlyList<KycIdentity>>> GetByGlobalPersonKeyAsync(
        string globalPersonKey,
        CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE global_person_key = $_global_person_key ORDER BY created_date ASC")
                .WithParam("_t", KycIdentity.SchemaNameConst)
                .WithParam("_global_person_key", globalPersonKey);

            var rows = await _executor.QueryAsync<KycIdentity>(query, ct);
            return AZOAResult<IReadOnlyList<KycIdentity>>.Success(
                rows.Select(FromStorage).ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<IReadOnlyList<KycIdentity>>(
                exception,
                nameof(GetByGlobalPersonKeyAsync),
                "global-scope");
        }
    }

    public async Task<AZOAResult<IReadOnlyList<KycIdentity>>> GetErasureDueAsync(
        DateTimeOffset asOf,
        int limit,
        CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE erased_at = NONE AND retention_expires_at != NONE AND retention_expires_at <= $_as_of ORDER BY retention_expires_at ASC LIMIT $_limit")
                .WithParam("_t", KycIdentity.SchemaNameConst)
                .WithParam("_as_of", asOf.ToUniversalTime())
                .WithParam("_limit", Math.Clamp(limit, 1, 1000));

            var rows = await _executor.QueryAsync<KycIdentity>(query, ct);
            return AZOAResult<IReadOnlyList<KycIdentity>>.Success(
                rows.Select(FromStorage).ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<IReadOnlyList<KycIdentity>>(
                exception,
                nameof(GetErasureDueAsync),
                asOf);
        }
    }

    public async Task<AZOAResult<KycIdentity>> EraseAsync(
        string recordId,
        string reason,
        DateTimeOffset erasedAt,
        CancellationToken ct = default)
    {
        try
        {
            // Explicit SET clause: person_key, global_person_key, user_ids,
            // provenance, and created_date are deliberately NOT touched. Erasure
            // removes the readable personal attributes; it must not destroy the
            // pseudonymous key that uniqueness and the audit trail depend on.
            var query = SurrealQuery
                .Of("""
                    UPDATE type::record($_t, $_id)
                    SET first_name = NONE,
                        last_name = NONE,
                        birthday = NONE,
                        citizenship = NONE,
                        erased_at = $_erased_at,
                        erasure_reason = $_reason,
                        modified_date = $_erased_at
                    WHERE erased_at = NONE
                    RETURN AFTER
                    """)
                .WithParam("_t", KycIdentity.SchemaNameConst)
                .WithParam("_id", recordId)
                .WithParam("_erased_at", erasedAt.ToUniversalTime())
                .WithParam("_reason", reason);

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();

            var erased = response.GetValues<KycIdentity>(0).FirstOrDefault();
            if (erased is not null)
                return AZOAResult<KycIdentity>.Success(FromStorage(erased), "Erased.");

            // Already erased is success: erasure is idempotent by intent.
            var current = await GetByIdAsync(recordId, ct);
            return current.IsError || current.Result is null
                ? AZOAResult<KycIdentity>.FailureWithCode(PublicStoreError, AzoaErrorCodes.NotFound)
                : AZOAResult<KycIdentity>.Success(current.Result, "Already erased.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<KycIdentity>(exception, nameof(EraseAsync), recordId);
        }
    }

    private async Task<AZOAResult<KycIdentity>> GetByIdAsync(string recordId, CancellationToken ct)
    {
        var query = SurrealQuery.SelectById(KycIdentity.SchemaNameConst, recordId);
        var row = await _executor.QuerySingleAsync<KycIdentity>(query, ct);
        return new AZOAResult<KycIdentity>
        {
            Message = row is null ? "No identity binding found." : "Success",
            Result = row is null ? null : FromStorage(row),
        };
    }

    private AZOAResult<T> StoreFailure<T>(Exception exception, string operation, object entityId)
    {
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        // entityId is a scope or an opaque digest, never a person key or a document
        // number, so it is safe to correlate on.
        _logger.LogError(
            exception,
            "KYC identity persistence failure in {Operation}; correlation={CorrelationId}; entity={EntityId}",
            operation,
            correlationId,
            entityId);
        return AZOAResult<T>.FailureWithCode(PublicStoreError, AzoaErrorCodes.DependencyUnavailable);
    }

    private static KycIdentity ToStorage(KycIdentity identity) => new()
    {
        Id = identity.Id,
        TenantScope = identity.TenantScope,
        TenantId = string.IsNullOrWhiteSpace(identity.TenantId)
            ? null
            : SurrealLink.ToLink("user", identity.TenantId),
        PersonKey = identity.PersonKey,
        GlobalPersonKey = identity.GlobalPersonKey,
        PersonKeyVersion = identity.PersonKeyVersion,
        UserIds = identity.UserIds,
        FirstName = identity.FirstName,
        LastName = identity.LastName,
        Birthday = identity.Birthday,
        Citizenship = identity.Citizenship,
        Provider = identity.Provider,
        ProviderKey = identity.ProviderKey,
        RetentionExpiresAt = identity.RetentionExpiresAt?.ToUniversalTime(),
        ErasedAt = identity.ErasedAt?.ToUniversalTime(),
        ErasureReason = identity.ErasureReason,
        CreatedDate = identity.CreatedDate.ToUniversalTime(),
        ModifiedDate = identity.ModifiedDate?.ToUniversalTime(),
    };

    private static KycIdentity FromStorage(KycIdentity identity) => new()
    {
        Id = identity.Id,
        TenantScope = identity.TenantScope,
        TenantId = string.IsNullOrWhiteSpace(identity.TenantId)
            ? null
            : SurrealLink.FromLink(identity.TenantId),
        PersonKey = identity.PersonKey,
        GlobalPersonKey = identity.GlobalPersonKey,
        PersonKeyVersion = identity.PersonKeyVersion,
        UserIds = identity.UserIds,
        FirstName = identity.FirstName,
        LastName = identity.LastName,
        Birthday = identity.Birthday,
        Citizenship = identity.Citizenship,
        Provider = identity.Provider,
        ProviderKey = identity.ProviderKey,
        RetentionExpiresAt = identity.RetentionExpiresAt,
        ErasedAt = identity.ErasedAt,
        ErasureReason = identity.ErasureReason,
        CreatedDate = identity.CreatedDate,
        ModifiedDate = identity.ModifiedDate,
    };
}
