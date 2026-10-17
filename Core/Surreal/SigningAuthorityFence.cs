using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Signing;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Core.Surreal;

/// <summary>Locks exact credential, wallet and selected-grant witnesses within an economic transaction.</summary>
public static class SigningAuthorityFence
{
    public const string FailureMarker = "SIGNING_AUTHORITY_CHANGED";

    /// <summary>Checks current exact consent for a local mutation without requesting a signing wallet.</summary>
    public static IReadOnlyList<SurrealQuery>? ForConsent(SigningConsentWitness witness, IReadOnlyList<string> requiredScopes, string prefix)
    {
        if (witness.Version != 1 || !witness.RequiredScopes.Order(StringComparer.Ordinal).SequenceEqual(requiredScopes.Order(StringComparer.Ordinal))) return null;
        var result = Credential(witness.Credential, prefix, true);
        return result is null || !Grants(result, witness.Credential, witness.RequiredScopes, witness.Grants, prefix) ? null : result;
    }

    /// <summary>Checks an authenticated credential while the original workflow is admitted.</summary>
    public static IReadOnlyList<SurrealQuery>? ForRunAdmission(SigningCredentialWitness credential, string prefix)
        => Credential(credential, prefix, true);

    /// <summary>Checks the saved credential ceiling and selected grants during atomic economic node admission.</summary>
    public static IReadOnlyList<SurrealQuery>? ForNodeAdmission(WorkflowSigningWitness witness, string prefix)
    {
        if (witness.Version != 1 || witness.Action.ActorUserId != witness.Credential.ActorUserId
            || witness.Action.ActingTenantId != witness.Credential.TenantId) return null;
        var result = Credential(witness.Credential, prefix, false);
        return result is null || !Grants(result, witness.Credential, witness.RequiredScopes, witness.Grants, prefix) ? null : result;
    }

    /// <summary>Checks current authority for a new economic effect, including exact original workflow witnesses.</summary>
    public static IReadOnlyList<SurrealQuery>? Create(SigningAdmissionAuthority authority, IReadOnlyList<string> requiredScopes, string prefix)
    {
        var expectedScopes = authority.Kind == "Workflow" ? requiredScopes.Append(AzoaScopes.ActionExecute).Distinct(StringComparer.Ordinal) : requiredScopes;
        if (authority.Version != 1 || authority.Wallet.OwnerUserId != authority.Credential.ActorUserId
            || authority.Wallet.Id == Guid.Empty || string.IsNullOrWhiteSpace(authority.Wallet.Address)
            || string.IsNullOrWhiteSpace(authority.Wallet.ChainType)
            || !expectedScopes.Order(StringComparer.Ordinal).SequenceEqual(authority.RequiredScopes.Order(StringComparer.Ordinal))) return null;
        var workflow = authority.Kind == "Workflow";
        if (!workflow && authority.Kind != (authority.Credential.Kind == "Jwt" ? "DirectJwt" : "DirectApiKey")) return null;
        var result = Credential(authority.Credential, prefix, !workflow);
        if (result is null || !Grants(result, authority.Credential, authority.RequiredScopes, authority.Grants, prefix)) return null;
        if (workflow)
        {
            if (authority.Action is not { } action || action.ActorUserId != authority.Credential.ActorUserId
                || action.ActingTenantId != authority.Credential.TenantId) return null;
            result.AddRange(ActionExecutionFence.Create(action.RunId.ToString("N"), action.NodeId.ToString("N"),
                action.ActorUserId.ToString("N"), action.ActingTenantId?.ToString("N"), action.IntentHash));
            var witness = new WorkflowSigningWitness(1, authority.Credential, action, authority.RequiredScopes, authority.Grants, authority.WorkflowAuthorizationId);
            result.Add(Sql("LET $_sa_node = $_effect_execution[0]", prefix));
            result.Add(Sql("""
                IF $_sa_node = NONE OR $_sa_node.signing_authority_json = NONE
                    OR crypto::sha256($_sa_node.signing_authority_json) != $_sa_witness_hash { THROW 'SIGNING_AUTHORITY_CHANGED' }
                """, prefix).WithParam(prefix + "_witness_hash", CanonicalJson.Hash(witness)));
        }
        else if (authority.Action is not null || authority.WorkflowAuthorizationId is not null) return null;
        result.Add(Sql("""
            LET $_sa_wallet = (UPDATE ONLY type::record('wallet', $_sa_wallet_id) SET address = address
                WHERE user_id = type::record('user', $_sa_actor_id) AND address = $_sa_address AND chain_type = $_sa_chain RETURN AFTER)
            """, prefix).WithParam(prefix + "_wallet_id", authority.Wallet.Id.ToString("N"))
                .WithParam(prefix + "_actor_id", authority.Credential.ActorUserId.ToString("N"))
                .WithParam(prefix + "_address", authority.Wallet.Address).WithParam(prefix + "_chain", authority.Wallet.ChainType));
        result.Add(Sql("IF $_sa_wallet = NONE { THROW 'SIGNING_AUTHORITY_CHANGED' }", prefix));
        return result;
    }

    // raw: current credential/key/grant/wallet locks join the owning admission or first-send transaction.
    internal static List<SurrealQuery>? Credential(SigningCredentialWitness value, string prefix, bool direct)
    {
        ValidatePrefix(prefix);
        if (value.Version != 1 || value.ActorUserId == Guid.Empty || value.TenantId == Guid.Empty
            || value.Kind is not ("Jwt" or "ApiKey") || value.Scopes is null || value.Scopes.Any(string.IsNullOrWhiteSpace)) return null;
        if (value.Kind == "Jwt" && (value.IssuedAt is null || value.ExpiresAt is null || value.IssuedAt >= value.ExpiresAt
            || value.ApiKeyId is not null || value.ApiKeyIdentityHash is not null || value.StoredApiKeyScopes is not null
            || value.TokenUse is not (AzoaClaims.TokenUseLogin or AzoaClaims.TokenUseChild or AzoaClaims.TokenUseNodeOperator)
            || (value.TokenUse == AzoaClaims.TokenUseChild) != (value.TenantId is not null)
            || value.OwnerAuthority != (value.TokenUse == AzoaClaims.TokenUseLogin))) return null;
        if (value.Kind == "ApiKey" && (value.ApiKeyId is null || value.ApiKeyId == Guid.Empty
            || string.IsNullOrWhiteSpace(value.ApiKeyIdentityHash) || value.StoredApiKeyScopes is null || value.TenantId is not null
            || value.IssuedAt is not null || value.TokenUse is not null || value.OperatorCredentialRevision is not null || value.OperatorSessionRevision is not null
            || (value.OwnerAuthority && (value.StoredApiKeyScopes.Count != 0 || value.Scopes.Count != 0))
            || (value.StoredApiKeyScopes.Count != 0 && value.Scopes.Any(scope => !value.StoredApiKeyScopes.Contains(scope, StringComparer.Ordinal))))) return null;
        if (value.TenantId is not null && value.OwnerAuthority) return null;
        if (value.TokenUse != AzoaClaims.TokenUseNodeOperator && (value.OperatorCredentialRevision is not null || value.OperatorSessionRevision is not null)) return null;
        if (value.AuthNotBefore < DateTimeOffset.MinValue.AddSeconds(1)) return null;
        if (value.TokenUse == AzoaClaims.TokenUseNodeOperator
            && (value.ActorUserId != AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.UserId || value.OperatorCredentialRevision is null or < 0
                || value.OperatorSessionRevision is null or < 0)) return null;
        var result = new List<SurrealQuery>
        {
            Sql("""
                LET $_sa_actor = (UPDATE ONLY type::record('user', $_sa_actor_id) SET is_active = is_active
                    WHERE is_active = true AND (($_sa_watermark_present AND auth_not_before = $_sa_watermark)
                        OR (!$_sa_watermark_present AND auth_not_before = NONE)) RETURN AFTER)
                """, prefix).WithParam(prefix + "_actor_id", value.ActorUserId.ToString("N"))
                .WithParam(prefix + "_watermark_present", value.AuthNotBefore.HasValue)
                .WithParam(prefix + "_watermark", value.AuthNotBefore ?? DateTimeOffset.UnixEpoch),
            Sql("IF $_sa_actor = NONE { THROW 'SIGNING_AUTHORITY_CHANGED' }", prefix)
        };
        if (value.Kind == "Jwt")
        {
            result.Add(Sql("""
                IF $_sa_issued > time::now() OR ($_sa_direct AND $_sa_expires <= time::now())
                    OR ($_sa_watermark_present AND $_sa_issued < $_sa_cutoff) { THROW 'SIGNING_AUTHORITY_CHANGED' }
                """, prefix).WithParam(prefix + "_issued", value.IssuedAt!.Value).WithParam(prefix + "_expires", value.ExpiresAt!.Value)
                    .WithParam(prefix + "_direct", direct).WithParam(prefix + "_watermark_present", value.AuthNotBefore.HasValue)
                    .WithParam(prefix + "_cutoff", value.AuthNotBefore?.AddSeconds(-1) ?? DateTimeOffset.UnixEpoch));
            if (value.TokenUse == AzoaClaims.TokenUseNodeOperator)
            {
                result.Add(Sql("""
                    LET $_sa_operator = (UPDATE ONLY admin_bootstrap_state:local SET session_revision = session_revision
                        WHERE user_id = type::record('user', $_sa_actor_id) AND credential_revision = $_sa_operator_revision
                            AND session_revision = $_sa_operator_session RETURN AFTER)
                    """, prefix).WithParam(prefix + "_actor_id", value.ActorUserId.ToString("N"))
                        .WithParam(prefix + "_operator_revision", value.OperatorCredentialRevision!.Value)
                        .WithParam(prefix + "_operator_session", value.OperatorSessionRevision!.Value));
                result.Add(Sql("IF $_sa_operator = NONE { THROW 'SIGNING_AUTHORITY_CHANGED' }", prefix));
            }
        }
        else
        {
            result.Add(Sql("""
                LET $_sa_key = (UPDATE ONLY type::record('api_key', $_sa_key_id) SET is_active = is_active
                    WHERE user_id = type::record('user', $_sa_actor_id) AND is_active = true AND revoked_at = NONE
                        AND key_hash = $_sa_key_hash AND scopes = $_sa_key_scopes
                        AND (expires_at = NONE OR expires_at > time::now())
                        AND (($_sa_key_expiry_present AND expires_at = $_sa_key_expiry)
                            OR (!$_sa_key_expiry_present AND expires_at = NONE)) RETURN AFTER)
                """, prefix).WithParam(prefix + "_key_id", value.ApiKeyId!.Value.ToString("N"))
                    .WithParam(prefix + "_actor_id", value.ActorUserId.ToString("N"))
                    .WithParam(prefix + "_key_hash", value.ApiKeyIdentityHash).WithParam(prefix + "_key_scopes", value.StoredApiKeyScopes)
                    .WithParam(prefix + "_key_expiry_present", value.ExpiresAt.HasValue)
                    .WithParam(prefix + "_key_expiry", value.ExpiresAt ?? DateTimeOffset.UnixEpoch));
            result.Add(Sql("IF $_sa_key = NONE { THROW 'SIGNING_AUTHORITY_CHANGED' }", prefix));
        }
        return result;
    }

    internal static bool Grants(List<SurrealQuery> result, SigningCredentialWitness credential, IReadOnlyList<string> scopes,
        IReadOnlyList<SigningGrantWitness> grants, string prefix)
    {
        if (scopes is null || grants is null || scopes.Count == 0 || scopes.Any(string.IsNullOrWhiteSpace) || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Count
            || (!credential.OwnerAuthority && scopes.Any(scope => !credential.Scopes.Contains(scope, StringComparer.Ordinal)))) return false;
        if (credential.TenantId is null) return grants.Count == 0;
        if (grants.Count != scopes.Count || !grants.Select(grant => grant.RequiredScope).Order(StringComparer.Ordinal).SequenceEqual(scopes.Order(StringComparer.Ordinal))) return false;
        result.Add(Sql("""
            LET $_sa_tenant = (UPDATE ONLY type::record('user', $_sa_tenant_id) SET is_active = is_active WHERE is_active = true RETURN AFTER)
            """, prefix).WithParam(prefix + "_tenant_id", credential.TenantId.Value.ToString("N")));
        result.Add(Sql("IF $_sa_tenant = NONE { THROW 'SIGNING_AUTHORITY_CHANGED' }", prefix));
        for (var index = 0; index < grants.Count; index++)
        {
            var grant = grants[index];
            if (grant.Id == Guid.Empty || !grant.StoredScopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(grant.RequiredScope, StringComparer.Ordinal)) return false;
            var nested = prefix + "_grant" + index;
            result.Add(Sql("""
                LET $_sa_row = (UPDATE ONLY type::record('consent_grant', $_sa_id) SET scopes = scopes
                    WHERE grantor_user_id = type::record('user', $_sa_actor) AND tenant_id = type::record('user', $_sa_tenant)
                        AND origin = 'UserExplicit' AND scopes = $_sa_scopes AND granted_at = $_sa_granted AND revoked_at = NONE
                        AND (expires_at = NONE OR expires_at > time::now())
                        AND (($_sa_expiry_present AND expires_at = $_sa_expiry) OR (!$_sa_expiry_present AND expires_at = NONE)) RETURN AFTER)
                """, nested).WithParam(nested + "_id", grant.Id.ToString("N")).WithParam(nested + "_actor", credential.ActorUserId.ToString("N"))
                    .WithParam(nested + "_tenant", credential.TenantId.Value.ToString("N")).WithParam(nested + "_scopes", grant.StoredScopes)
                    .WithParam(nested + "_granted", grant.GrantedAt).WithParam(nested + "_expiry_present", grant.ExpiresAt.HasValue)
                    .WithParam(nested + "_expiry", grant.ExpiresAt ?? DateTimeOffset.UnixEpoch));
            result.Add(Sql("IF $_sa_row = NONE { THROW 'SIGNING_AUTHORITY_CHANGED' }", nested));
        }
        return true;
    }

    // Rewritten names must equal the WithParam keys (prefix + "_x"); see Core/Surreal/AGENTS.md §Signing authority transaction fences.
    private static SurrealQuery Sql(string template, string prefix) => SurrealQuery.Of(template.Replace("$_sa_", "$" + prefix + "_", StringComparison.Ordinal));
    private static void ValidatePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 48 || prefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("A short alphanumeric statement parameter prefix is required.", nameof(prefix));
    }
}
