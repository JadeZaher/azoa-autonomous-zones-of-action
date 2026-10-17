using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Helpers;

/// <summary>Fences a provider send against its durable attempt and tenant authority.</summary>
public static class HostedKycCreationFence
{
    public const string FailureMarker = "HOSTED_CREATION_AUTHORITY_CHANGED";

    // raw: cross-record attempt/authority ownership in the hosted send transaction; see AGENTS.md.
    public static IReadOnlyList<SurrealQuery> Create(HostedSession session) =>
    [
        SurrealQuery.Of("""
            LET $_send_attempt = (UPDATE type::record('kyc_submission', $_send_id)
                SET modified_date = modified_date
                WHERE status INSIDE ['PENDING', 'IN_REVIEW'] AND expires_at > time::now()
                    AND provider_session_id = $_send_id AND provider_key = $_send_provider
                    AND user_id = type::record('user', $_send_user)
                    AND ((tenant_id = NONE AND !$_send_has_tenant) OR ($_send_has_tenant AND tenant_id = type::record('user', $_send_tenant))) RETURN AFTER)
            """).WithParam("_send_id", session.Id).WithParam("_send_provider", session.ProviderKey)
            .WithParam("_send_user", session.SubjectUserId).WithParam("_send_tenant", session.TenantId ?? string.Empty).WithParam("_send_has_tenant", session.TenantId is not null),
        SurrealQuery.Of("IF array::len($_send_attempt) != 1 { THROW 'HOSTED_CREATION_AUTHORITY_CHANGED' }"),
        SurrealQuery.Of("""
            LET $_send_selection = (SELECT * FROM type::record('tenant_kyc_provider_selection', $_send_tenant)
                WHERE $_send_has_tenant)
            """).WithParam("_send_tenant", session.TenantId ?? string.Empty).WithParam("_send_has_tenant", session.TenantId is not null),
        SurrealQuery.Of("""
            LET $_send_profile = (SELECT * FROM type::record('kyc_provider_profile', $_send_provider)
                WHERE $_send_has_tenant)
            """).WithParam("_send_provider", session.ProviderKey).WithParam("_send_tenant", session.TenantId ?? string.Empty).WithParam("_send_has_tenant", session.TenantId is not null),
        SurrealQuery.Of("""
            IF $_send_has_tenant AND (array::len($_send_selection) != 1 OR array::len($_send_profile) != 1
                OR !$_send_profile[0].enabled OR $_send_selection[0].provider_key != $_send_provider
                OR $_send_selection[0].selection_version != $_send_attempt[0].provider_selection_version
                OR $_send_profile[0].trust_revision != $_send_attempt[0].provider_trust_revision)
                { THROW 'HOSTED_CREATION_AUTHORITY_CHANGED' }
            """).WithParam("_send_tenant", session.TenantId ?? string.Empty).WithParam("_send_has_tenant", session.TenantId is not null).WithParam("_send_provider", session.ProviderKey),
        SurrealQuery.Of("""
            IF $_send_has_tenant { UPSERT type::record('kyc_authority_guard', 'tenant-' + $_send_tenant)
                CONTENT { kind: 'tenant', authority_revision: $_send_attempt[0].provider_selection_version,
                    provider_key: $_send_provider, touched_at: time::now() } RETURN NONE }
            """).WithParam("_send_tenant", session.TenantId ?? string.Empty).WithParam("_send_has_tenant", session.TenantId is not null).WithParam("_send_provider", session.ProviderKey),
        SurrealQuery.Of("""
            IF $_send_has_tenant { UPSERT type::record('kyc_authority_guard', 'provider-' + $_send_provider)
                CONTENT { kind: 'provider', authority_revision: $_send_attempt[0].provider_trust_revision,
                    provider_key: $_send_provider, touched_at: time::now() } RETURN NONE }
            """).WithParam("_send_tenant", session.TenantId ?? string.Empty).WithParam("_send_has_tenant", session.TenantId is not null).WithParam("_send_provider", session.ProviderKey),
    ];
}
