using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed class SurrealKycControlStore : IKycControlStore
{
    private const string PublicStoreError =
        "KYC_CONTROL_STORE_UNAVAILABLE: KYC control persistence is temporarily unavailable.";
    private readonly ISurrealExecutor _executor;

    public SurrealKycControlStore(ISurrealExecutor executor)
    {
        _executor = executor;
    }

    public async Task<AZOAResult<IReadOnlyList<KycProviderProfile>>> ListProfilesAsync(
        CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery<KycProviderProfile>.From()
                .OrderBy(profile => profile.DisplayName);
            var rows = await _executor.QueryAsync<KycProviderProfile>(query, ct);
            return AZOAResult<IReadOnlyList<KycProviderProfile>>.Success(rows.ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DependencyFailure<IReadOnlyList<KycProviderProfile>>();
        }
    }

    public async Task<AZOAResult<KycProviderProfile?>> GetProfileAsync(
        string providerKey,
        CancellationToken ct = default)
    {
        try
        {
            var row = await _executor.QuerySingleAsync<KycProviderProfile>(
                SurrealQuery<KycProviderProfile>.Key(providerKey), ct);
            return AZOAResult<KycProviderProfile?>.Success(row);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DependencyFailure<KycProviderProfile?>();
        }
    }

    public async Task<AZOAResult<KycProviderProfile>> SaveProfileAsync(
        KycProviderProfile profile,
        KycControlAudit audit,
        long? expectedVersion,
        bool retireActiveAttempts,
        CancellationToken ct = default)
    {
        var stored = ToStorage(profile);
        var storedAudit = ToStorage(audit);
        // raw: profile version CAS and immutable audit append share one transaction.
        var transaction = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"),
            SurrealQuery
                .Of("LET $_current = SELECT VALUE version FROM type::record($_table, $_id)")
                .WithParam("_table", KycProviderProfile.SchemaNameConst)
                .WithParam("_id", profile.Id),
            SurrealQuery
                .Of("IF ($_has_expected AND (array::len($_current) != 1 OR $_current[0] != $_expected)) OR (!$_has_expected AND array::len($_current) != 0) { THROW 'KYC profile version conflict' }")
                .WithParam("_has_expected", expectedVersion.HasValue)
                .WithParam("_expected", expectedVersion ?? 0L),
            SurrealQuery
                .Of("UPSERT type::record($_table, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_table", KycProviderProfile.SchemaNameConst)
                .WithParam("_id", profile.Id)
                .WithParam("_body", stored),
            SurrealQuery
                .Of("UPSERT type::record($_guard_table, $_guard_id) CONTENT { kind: 'provider', authority_revision: $_revision, provider_key: $_provider, touched_at: $_now } RETURN NONE")
                .WithParam("_guard_table", "kyc_authority_guard")
                .WithParam("_guard_id", $"provider-{profile.Id}")
                .WithParam("_revision", profile.TrustRevision)
                .WithParam("_provider", profile.Id)
                .WithParam("_now", profile.UpdatedAt),
            SurrealQuery
                .Of("CREATE type::record($_audit_table, $_audit_id) CONTENT $_audit RETURN NONE")
                .WithParam("_audit_table", KycControlAudit.SchemaNameConst)
                .WithParam("_audit_id", audit.Id)
                .WithParam("_audit", storedAudit),
            SurrealQuery
                .Of("IF $_retire { UPDATE type::table($_submission_table) SET status = $_expired, modified_date = $_now WHERE provider_key = $_provider AND status INSIDE $_active RETURN NONE }")
                .WithParam("_retire", retireActiveAttempts)
                .WithParam("_submission_table", KycSubmission.SchemaNameConst)
                .WithParam("_expired", nameof(KycStatus.EXPIRED))
                .WithParam("_now", profile.UpdatedAt)
                .WithParam("_provider", profile.Id)
                .WithParam("_active", new[] { nameof(KycStatus.PENDING), nameof(KycStatus.IN_REVIEW) }),
            SurrealQuery.Of("COMMIT"));

        try
        {
            var response = await _executor.ExecuteAsync(transaction, ct);
            response.EnsureAllOk();
            var saved = response.GetValues<KycProviderProfile>(3).SingleOrDefault();
            return saved is null
                ? DependencyFailure<KycProviderProfile>()
                : AZOAResult<KycProviderProfile>.Success(saved, "KYC provider profile saved.");
        }
        catch (SurrealStatementException exception) when (
            exception.Message.Contains("profile version conflict", StringComparison.OrdinalIgnoreCase))
        {
            return AZOAResult<KycProviderProfile>.FailureWithCode(
                "KYC provider profile version conflict.",
                AzoaErrorCodes.Conflict);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DependencyFailure<KycProviderProfile>();
        }
    }

    public async Task<AZOAResult<TenantKycProviderSelection?>> GetSelectionAsync(
        Guid tenantId,
        CancellationToken ct = default)
    {
        try
        {
            var row = await _executor.QuerySingleAsync<TenantKycProviderSelection>(
                SurrealQuery<TenantKycProviderSelection>.Key(SurrealId.ToSurrealId(tenantId)), ct);
            return AZOAResult<TenantKycProviderSelection?>.Success(
                row is null ? null : FromStorage(row));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DependencyFailure<TenantKycProviderSelection?>();
        }
    }

    public async Task<AZOAResult<TenantKycProviderSelection>> SaveSelectionAsync(
        TenantKycProviderSelection selection,
        KycControlAudit audit,
        long? expectedVersion,
        CancellationToken ct = default)
    {
        var stored = ToStorage(selection);
        var storedAudit = ToStorage(audit);
        // raw: selection CAS, active-attempt retirement, and audit append are atomic.
        var transaction = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"),
            SurrealQuery
                .Of("LET $_current = SELECT VALUE selection_version FROM type::record($_table, $_id)")
                .WithParam("_table", TenantKycProviderSelection.SchemaNameConst)
                .WithParam("_id", selection.Id),
            SurrealQuery
                .Of("IF ($_has_expected AND (array::len($_current) != 1 OR $_current[0] != $_expected)) OR (!$_has_expected AND array::len($_current) != 0) { THROW 'Tenant KYC selection version conflict' }")
                .WithParam("_has_expected", expectedVersion.HasValue)
                .WithParam("_expected", expectedVersion ?? 0L),
            SurrealQuery
                .Of("UPSERT type::record($_table, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_table", TenantKycProviderSelection.SchemaNameConst)
                .WithParam("_id", selection.Id)
                .WithParam("_body", stored),
            SurrealQuery
                .Of("UPSERT type::record($_guard_table, $_guard_id) CONTENT { kind: 'tenant', authority_revision: $_revision, provider_key: $_provider, touched_at: $_now } RETURN NONE")
                .WithParam("_guard_table", "kyc_authority_guard")
                .WithParam("_guard_id", $"tenant-{selection.Id}")
                .WithParam("_revision", selection.SelectionVersion)
                .WithParam("_provider", selection.ProviderKey)
                .WithParam("_now", selection.UpdatedAt),
            SurrealQuery
                .Of("UPDATE type::table($_submission_table) SET status = $_expired, modified_date = $_now WHERE tenant_id = $_tenant AND status INSIDE $_active RETURN NONE")
                .WithParam("_submission_table", KycSubmission.SchemaNameConst)
                .WithParam("_expired", nameof(KycStatus.EXPIRED))
                .WithParam("_now", selection.UpdatedAt)
                .WithParam("_tenant", stored.TenantId)
                .WithParam("_active", new[] { nameof(KycStatus.PENDING), nameof(KycStatus.IN_REVIEW) }),
            SurrealQuery
                .Of("CREATE type::record($_audit_table, $_audit_id) CONTENT $_audit RETURN NONE")
                .WithParam("_audit_table", KycControlAudit.SchemaNameConst)
                .WithParam("_audit_id", audit.Id)
                .WithParam("_audit", storedAudit),
            SurrealQuery.Of("COMMIT"));

        try
        {
            var response = await _executor.ExecuteAsync(transaction, ct);
            response.EnsureAllOk();
            var saved = response.GetValues<TenantKycProviderSelection>(3).SingleOrDefault();
            return saved is null
                ? DependencyFailure<TenantKycProviderSelection>()
                : AZOAResult<TenantKycProviderSelection>.Success(
                    FromStorage(saved), "Tenant KYC provider selected.");
        }
        catch (SurrealStatementException exception) when (
            exception.Message.Contains("selection version conflict", StringComparison.OrdinalIgnoreCase))
        {
            return AZOAResult<TenantKycProviderSelection>.FailureWithCode(
                "Tenant KYC provider selection version conflict.",
                AzoaErrorCodes.Conflict);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DependencyFailure<TenantKycProviderSelection>();
        }
    }

    public async Task<AZOAResult<IReadOnlyList<TenantKycProviderSelection>>> ListSelectionsPageAsync(
        int offset,
        int limit,
        string? search,
        CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of("SELECT * FROM type::table($_table) WHERE $_search = '' OR string::lowercase(provider_key) CONTAINS $_search OR string::lowercase(<string>tenant_id) CONTAINS $_search ORDER BY updated_at DESC, id ASC START $_offset LIMIT $_limit")
                .WithParam("_table", TenantKycProviderSelection.SchemaNameConst)
                .WithParam("_search", search?.Trim().ToLowerInvariant() ?? string.Empty)
                .WithParam("_offset", Math.Max(0, offset))
                .WithParam("_limit", Math.Clamp(limit, 1, 101));
            var rows = await _executor.QueryAsync<TenantKycProviderSelection>(query, ct);
            return AZOAResult<IReadOnlyList<TenantKycProviderSelection>>.Success(
                rows.Select(FromStorage).ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DependencyFailure<IReadOnlyList<TenantKycProviderSelection>>();
        }
    }

    public async Task<AZOAResult<long>> CountSelectionsAsync(CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of("SELECT VALUE count() FROM type::table($_table) GROUP ALL")
                .WithParam("_table", TenantKycProviderSelection.SchemaNameConst);
            var values = await _executor.QueryAsync<long>(query, ct);
            return AZOAResult<long>.Success(values.SingleOrDefault());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DependencyFailure<long>();
        }
    }

    public async Task<AZOAResult<IReadOnlyList<KycControlAudit>>> ListAuditPageAsync(
        int limit,
        KycControlAuditCursor? before,
        Guid? tenantId,
        string? providerKey,
        string? action,
        CancellationToken ct = default)
    {
        try
        {
            // raw: filtered composite keyset pagination; see Providers/Stores/Surreal/AGENTS.md.
            var query = SurrealQuery
                .Of("SELECT * FROM type::table($_table) WHERE (!$_has_tenant OR tenant_id = $_tenant) AND ($_provider = '' OR provider_key = $_provider) AND ($_action = '' OR action = $_action) AND (!$_has_before OR occurred_at < $_before_occurred_at OR (occurred_at = $_before_occurred_at AND id < type::record($_table, $_before_id))) ORDER BY occurred_at DESC, id DESC LIMIT $_limit")
                .WithParam("_table", KycControlAudit.SchemaNameConst)
                .WithParam("_has_tenant", tenantId.HasValue)
                .WithParam("_tenant", tenantId.HasValue
                    ? SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(tenantId.Value))
                    : null)
                .WithParam("_provider", providerKey ?? string.Empty)
                .WithParam("_action", action ?? string.Empty)
                .WithParam("_has_before", before is not null)
                .WithParam("_before_occurred_at", before?.OccurredAt ?? DateTimeOffset.MaxValue)
                .WithParam("_before_id", before is null
                    ? "ffffffffffffffffffffffffffffffff"
                    : SurrealRecordGuid.BareId(before.RecordId))
                .WithParam("_limit", Math.Clamp(limit, 1, 101));
            var rows = await _executor.QueryAsync<KycControlAudit>(query, ct);
            return AZOAResult<IReadOnlyList<KycControlAudit>>.Success(
                rows.Select(FromStorage).ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DependencyFailure<IReadOnlyList<KycControlAudit>>();
        }
    }

    private static AZOAResult<T> DependencyFailure<T>()
        => AZOAResult<T>.FailureWithCode(PublicStoreError, AzoaErrorCodes.DependencyUnavailable);

    private static KycProviderProfile ToStorage(KycProviderProfile profile) => new()
    {
        Id = profile.Id,
        DisplayName = profile.DisplayName,
        AdapterKey = profile.AdapterKey,
        Enabled = profile.Enabled,
        PolicyVersion = profile.PolicyVersion,
        AssuranceLevel = profile.AssuranceLevel,
        Version = profile.Version,
        TrustRevision = profile.TrustRevision,
        UpdatedByAvatarId = SurrealLink.ToLink("avatar", profile.UpdatedByAvatarId),
        CreatedAt = profile.CreatedAt,
        UpdatedAt = profile.UpdatedAt,
    };

    private static TenantKycProviderSelection ToStorage(TenantKycProviderSelection selection) => new()
    {
        Id = selection.Id,
        TenantId = SurrealLink.ToLink("avatar", selection.TenantId),
        ProviderKey = selection.ProviderKey,
        SelectionVersion = selection.SelectionVersion,
        UpdatedByAvatarId = SurrealLink.ToLink("avatar", selection.UpdatedByAvatarId),
        CreatedAt = selection.CreatedAt,
        UpdatedAt = selection.UpdatedAt,
    };

    private static TenantKycProviderSelection FromStorage(TenantKycProviderSelection selection) => new()
    {
        Id = selection.Id,
        TenantId = SurrealLink.FromLink(selection.TenantId),
        ProviderKey = selection.ProviderKey,
        SelectionVersion = selection.SelectionVersion,
        UpdatedByAvatarId = SurrealLink.FromLink(selection.UpdatedByAvatarId),
        CreatedAt = selection.CreatedAt,
        UpdatedAt = selection.UpdatedAt,
    };

    private static KycControlAudit ToStorage(KycControlAudit audit) => new()
    {
        Id = audit.Id,
        Action = audit.Action,
        TenantId = string.IsNullOrWhiteSpace(audit.TenantId)
            ? null
            : SurrealLink.ToLink("avatar", audit.TenantId),
        ProviderKey = audit.ProviderKey,
        PreviousProviderKey = audit.PreviousProviderKey,
        Version = audit.Version,
        PreviousDisplayName = audit.PreviousDisplayName,
        DisplayName = audit.DisplayName,
        PreviousAdapterKey = audit.PreviousAdapterKey,
        AdapterKey = audit.AdapterKey,
        PreviousEnabled = audit.PreviousEnabled,
        Enabled = audit.Enabled,
        PreviousPolicyVersion = audit.PreviousPolicyVersion,
        PolicyVersion = audit.PolicyVersion,
        PreviousAssuranceLevel = audit.PreviousAssuranceLevel,
        AssuranceLevel = audit.AssuranceLevel,
        PreviousTrustRevision = audit.PreviousTrustRevision,
        TrustRevision = audit.TrustRevision,
        ActorAvatarId = SurrealLink.ToLink("avatar", audit.ActorAvatarId),
        OccurredAt = audit.OccurredAt,
    };

    private static KycControlAudit FromStorage(KycControlAudit audit) => new()
    {
        Id = audit.Id,
        Action = audit.Action,
        TenantId = string.IsNullOrWhiteSpace(audit.TenantId)
            ? null
            : SurrealLink.FromLink(audit.TenantId),
        ProviderKey = audit.ProviderKey,
        PreviousProviderKey = audit.PreviousProviderKey,
        Version = audit.Version,
        PreviousDisplayName = audit.PreviousDisplayName,
        DisplayName = audit.DisplayName,
        PreviousAdapterKey = audit.PreviousAdapterKey,
        AdapterKey = audit.AdapterKey,
        PreviousEnabled = audit.PreviousEnabled,
        Enabled = audit.Enabled,
        PreviousPolicyVersion = audit.PreviousPolicyVersion,
        PolicyVersion = audit.PolicyVersion,
        PreviousAssuranceLevel = audit.PreviousAssuranceLevel,
        AssuranceLevel = audit.AssuranceLevel,
        PreviousTrustRevision = audit.PreviousTrustRevision,
        TrustRevision = audit.TrustRevision,
        ActorAvatarId = SurrealLink.FromLink(audit.ActorAvatarId),
        OccurredAt = audit.OccurredAt,
    };
}
