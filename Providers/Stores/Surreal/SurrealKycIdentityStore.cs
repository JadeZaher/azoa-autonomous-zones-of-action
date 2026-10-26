// SPDX-License-Identifier: UNLICENSED

using SurrealForge.Client;
using SurrealForge.Client.Query;
using SurrealForge.Client.Idempotency;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealKycIdentityStore : IKycIdentityStore
{
    private readonly ISurrealExecutor _executor;

    public SurrealKycIdentityStore(ISurrealExecutor executor, ILogger<SurrealKycIdentityStore>? logger = null)
        => _executor = executor;

    /// <inheritdoc/>
    public async Task<AZOAResult<KycIdentity>> BindUserAsync(KycIdentity candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        try
        {
            var created = await KycEvidenceFence.ExecuteAsync(_executor, KycEvidenceFence.Mutation(candidate.UserIds ?? [], candidate.TenantId,
                SurrealWriter.Create(ToStorage(candidate))), ct);
            created.EnsureAllOk();
            return AZOAResult<KycIdentity>.Success(FromStorage(created.GetValues<KycIdentity>(2).Single()));
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var existing = await _executor.QuerySingleAsync<KycIdentity>(SurrealQuery<KycIdentity>.Key(candidate.Id), ct);
            if (existing is null) throw;
            var user = candidate.UserIds?.SingleOrDefault();
            if (string.IsNullOrWhiteSpace(user) || (existing.UserIds?.Count > 0 && !existing.UserIds.Contains(user)))
                return AZOAResult<KycIdentity>.Success(FromStorage(existing));
            var query = SurrealWriter.UpdateOnly<KycIdentity>(candidate.Id)
                .Where(row => row.UserIds == existing.UserIds)
                .Set(row => row.UserIds, candidate.UserIds);
            query = candidate.FirstName is null ? query.Unset(row => row.FirstName) : query.Set(row => row.FirstName, candidate.FirstName);
            query = candidate.LastName is null ? query.Unset(row => row.LastName) : query.Set(row => row.LastName, candidate.LastName);
            query = candidate.Birthday is null ? query.Unset(row => row.Birthday) : query.Set(row => row.Birthday, candidate.Birthday);
            query = candidate.Citizenship is null ? query.Unset(row => row.Citizenship) : query.Set(row => row.Citizenship, candidate.Citizenship);
            query = candidate.RetentionExpiresAt is null ? query.Unset(row => row.RetentionExpiresAt) : query.Set(row => row.RetentionExpiresAt, candidate.RetentionExpiresAt);
            query = candidate.ModifiedDate is null ? query.Unset(row => row.ModifiedDate) : query.Set(row => row.ModifiedDate, candidate.ModifiedDate);

            var response = await KycEvidenceFence.ExecuteAsync(_executor, KycEvidenceFence.Mutation(candidate.UserIds ?? [], candidate.TenantId, query.Build()), ct);
            response.EnsureAllOk();
            var saved = response.GetValues<KycIdentity>(2).SingleOrDefault();
            return saved is null
                ? AZOAResult<KycIdentity>.FailureWithCode("The identity binding changed.", AzoaErrorCodes.Conflict)
                : AZOAResult<KycIdentity>.Success(FromStorage(saved));
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<KycIdentity>> BindVerifiedUserAsync(
        KycIdentity candidate, KycSubmission expected, HostedSession lease, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lease.ResumeLeaseToken) || expected.ProviderSessionId != lease.Id
            || expected.Status is not (AZOA.WebAPI.Models.Kyc.KycStatus.PENDING or AZOA.WebAPI.Models.Kyc.KycStatus.IN_REVIEW)
            || candidate.UserIds?.Count != 1 || candidate.UserIds[0] != expected.UserId
            || candidate.TenantId != expected.TenantId || candidate.ProviderKey != expected.ProviderKey)
            return AZOAResult<KycIdentity>.FailureWithCode("The verified attempt changed.", AzoaErrorCodes.Conflict);
        // raw: atomic lease + active-attempt fencing + identity membership; see AGENTS.md.
        var fence = HostedSessionLeaseFence.CreateGuard(lease);
        // $_attempt_provider_key, not $_provider: Combine shares one parameter namespace, and
        // BuildBindingQuery also defines $_provider (the enum name) — the clobber made this
        // fence match zero rows on every live approval (mvp-tandem-demo F4).
        var attempt = SurrealQuery.Of("""
            LET $_attempt = (UPDATE type::record($_attempt_table, $_attempt_id)
                SET provider_trust_revision = provider_trust_revision
                WHERE user_id = $_user AND provider_key = $_attempt_provider_key AND provider_session_id = $_session
                    AND (($_has_tenant AND tenant_id = $_tenant) OR (!$_has_tenant AND tenant_id = NONE))
                    AND status = $_status AND expires_at > time::now()
                    AND provider_selection_version = $_selection AND provider_trust_revision = $_trust
                RETURN AFTER)
            """)
            .WithParam("_attempt_table", KycSubmission.SchemaNameConst)
            .WithParam("_attempt_id", expected.Id)
            .WithParam("_user", SurrealRecordParam.Of("user", expected.UserId))
            .WithParam("_attempt_provider_key", expected.ProviderKey)
            .WithParam("_session", lease.Id)
            .WithParam("_has_tenant", expected.TenantId is not null)
            .WithParam("_tenant", SurrealRecordParam.Of("user", expected.TenantId))
            .WithParam("_status", expected.Status.ToString())
            .WithParam("_selection", expected.ProviderSelectionVersion)
            .WithParam("_trust", expected.ProviderTrustRevision);
        var transaction = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"), KycEvidenceFence.Create(expected.UserId, expected.TenantId), fence,
            HostedSessionLeaseFence.AssertCurrent(),
            attempt,
            SurrealQuery.Of("IF array::len($_attempt) != 1 { THROW 'KYC_BINDING_AUTHORITY_LOST' }"),
            BuildBindingQuery(candidate), SurrealQuery.Of("COMMIT"));
        try
        {
            var response = await KycEvidenceFence.ExecuteAsync(_executor, transaction, ct);
            response.EnsureAllOk();
            var saved = response.GetValues<KycIdentity>(6).SingleOrDefault();
            return saved is null
                ? AZOAResult<KycIdentity>.FailureWithCode("The identity binding could not be applied.", AzoaErrorCodes.OperationNotAllowed)
                : AZOAResult<KycIdentity>.Success(FromStorage(saved));
        }
        catch (SurrealStatementException exception) when ((exception.Message.Contains("KYC_BINDING_AUTHORITY_LOST", StringComparison.Ordinal)
                || exception.Message.Contains(HostedSessionLeaseFence.FailureMarker, StringComparison.Ordinal)))
        {
            return AZOAResult<KycIdentity>.FailureWithCode("The verified attempt changed.", AzoaErrorCodes.Conflict);
        }
    }

    private static SurrealQuery BuildBindingQuery(KycIdentity candidate)
    {
        var body = ToStorage(candidate);
        var newUser = candidate.UserIds?.FirstOrDefault();
        return SurrealQuery
            .Of("""
                UPSERT type::record($_t, $_id)
                SET tenant_scope = $_scope,
                    tenant_id = IF $_has_binding_tenant THEN $_binding_tenant ELSE NONE END,
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
                WHERE user_ids = NONE OR array::len(user_ids) = 0 OR $_new_user INSIDE user_ids
                RETURN AFTER
                """)
            .WithParam("_t", KycIdentity.SchemaNameConst)
            .WithParam("_id", body.Id)
            .WithParam("_scope", body.TenantScope)
            // _binding_tenant, not _tenant: the fence transaction shares one parameter
            // namespace (F4), and a first-party identity writes NONE — the column is
            // `none | record<user>` and a NULL param fails coercion (F11).
            .WithParam("_has_binding_tenant", !string.IsNullOrEmpty(candidate.TenantId))
            .WithParam("_binding_tenant", SurrealRecordParam.Of("user", candidate.TenantId))
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
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<KycIdentity>> GetByPersonKeyAsync(
        string tenantScope, string personKey, CancellationToken ct = default)
    {
        var row = await _executor.QuerySingleAsync<KycIdentity>(SurrealQuery<KycIdentity>.From()
            .Where(item => item.TenantScope == tenantScope && item.PersonKey == personKey).Limit(1), ct);
        return AZOAResult<KycIdentity>.Success(row is null ? null! : FromStorage(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<KycIdentity?>> GetByUserAsync(
        string tenantScope, string userId, CancellationToken ct = default)
    {
        var row = await _executor.QuerySingleAsync<KycIdentity>(SurrealQuery<KycIdentity>.From()
            .Where(item => item.TenantScope == tenantScope && item.UserIds!.Contains(userId)).Limit(1), ct);
        return AZOAResult<KycIdentity?>.Success(row is null ? null : FromStorage(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IReadOnlyList<KycIdentity>>> GetByGlobalPersonKeyAsync(
        string globalPersonKey, CancellationToken ct = default)
    {
        var rows = await _executor.QueryAsync<KycIdentity>(SurrealQuery<KycIdentity>.From()
            .Where(item => item.GlobalPersonKey == globalPersonKey).OrderBy(item => item.CreatedDate), ct);
        return AZOAResult<IReadOnlyList<KycIdentity>>.Success(rows.Select(FromStorage).ToList());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IReadOnlyList<KycIdentity>>> GetErasureDueAsync(
        DateTimeOffset asOf, int limit, CancellationToken ct = default)
    {
        var rows = await _executor.QueryAsync<KycIdentity>(SurrealQuery<KycIdentity>.From()
            .Where(item => item.ErasedAt == null && item.RetentionExpiresAt != null && item.RetentionExpiresAt <= asOf)
            .OrderBy(item => item.RetentionExpiresAt!).Limit(Math.Clamp(limit, 1, 1000)), ct);
        return AZOAResult<IReadOnlyList<KycIdentity>>.Success(rows.Select(FromStorage).ToList());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<KycIdentity>> EraseAsync(
        string recordId, string reason, DateTimeOffset erasedAt, CancellationToken ct = default)
    {
        var existing = await _executor.QuerySingleAsync<KycIdentity>(SurrealQuery<KycIdentity>.Key(recordId), ct);
        if (existing is null) return AZOAResult<KycIdentity>.FailureWithCode("Identity binding not found.", AzoaErrorCodes.NotFound);
        var query = SurrealWriter.UpdateOnly<KycIdentity>(recordId).Where(item => item.ErasedAt == null)
            .Where(item => item.UserIds == existing.UserIds)
            .Unset(item => item.FirstName).Unset(item => item.LastName)
            .Unset(item => item.Birthday).Unset(item => item.Citizenship)
            .Set(item => item.ErasedAt, (DateTimeOffset?)erasedAt)
            .Set(item => item.ErasureReason, reason).Set(item => item.ModifiedDate, (DateTimeOffset?)erasedAt).Build();
        var response = await KycEvidenceFence.ExecuteAsync(_executor, KycEvidenceFence.Mutation(existing.UserIds ?? [], existing.TenantId, query), ct);
        response.EnsureAllOk();
        var row = response.GetValues<KycIdentity>(2).SingleOrDefault()
            ?? await _executor.QuerySingleAsync<KycIdentity>(SurrealQuery<KycIdentity>.Key(recordId), ct);
        return row is null
            ? AZOAResult<KycIdentity>.FailureWithCode("Identity binding not found.", AzoaErrorCodes.NotFound)
            : row.ErasedAt is null ? AZOAResult<KycIdentity>.FailureWithCode("Identity membership changed before erasure.", AzoaErrorCodes.Conflict)
            : AZOAResult<KycIdentity>.Success(FromStorage(row));
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
