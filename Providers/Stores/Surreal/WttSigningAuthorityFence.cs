using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Wtt;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Binds shared signing/consent fences to the exact frozen WTT participant for each journal phase.</summary>
internal static class WttSigningAuthorityFence
{
    /// <summary>Scopes a workflow Bridge/Back node selects; the fence proves every grant on each first send.</summary>
    internal static readonly string[] WorkflowScopes = [AzoaScopes.WttSourceSign, AzoaScopes.WttTargetSign];

    internal static IReadOnlyList<SurrealQuery>? Stage(WttOperationIntent intent, string phase, string? originalPhase,
        WttStageAuthority authority, string prefix, WttActionBinding? action = null)
        => phase switch
        {
            "Source" => Signing(intent, intent.SourceAuthority, intent.Source.ChainType, authority, AzoaScopes.WttSourceSign, prefix, false, action),
            "Target" => Signing(intent, intent.TargetAuthority, intent.Target.ChainType, authority, AzoaScopes.WttTargetSign, prefix, false, action),
            // Tenant and workflow routes never carry a participant step (refused at quote), so the fence refuses them too.
            "Participant" or "NativeOutput" => action is null && intent.TenantId is null
                ? Consent(intent, authority, AzoaScopes.WttParticipantSign, prefix, false) : null,
            "Cleanup" => originalPhase switch
            {
                "Source" => Signing(intent, intent.SourceAuthority, intent.Source.ChainType, authority, AzoaScopes.WttCleanupSign, prefix, true, null),
                "Target" => Signing(intent, intent.TargetAuthority, intent.Target.ChainType, authority, AzoaScopes.WttCleanupSign, prefix, true, null),
                _ => null,
            },
            "Cancel" => Consent(intent, authority, AzoaScopes.WttSourceSign, prefix, true),
            _ => null,
        };

    private static IReadOnlyList<SurrealQuery>? Signing(WttOperationIntent intent, WttSigningAuthority owner, string chain,
        WttStageAuthority authority, string scope, string prefix, bool directOwnerOnly, WttActionBinding? action)
    {
        if (authority.Scope != scope || authority.Consent is not null || authority.Signing is not { } signing) return null;
        if (signing.Credential.ActorUserId != intent.ActorUserId
            || (directOwnerOnly ? signing.Credential.TenantId is not null : signing.Credential.TenantId != intent.TenantId)
            || signing.Wallet.Id != owner.Context.WalletId || signing.Wallet.OwnerUserId != intent.ActorUserId
            || !string.Equals(signing.Wallet.ChainType, chain, StringComparison.OrdinalIgnoreCase)
            || !ChainAddressIdentity.Equivalent(chain, signing.Wallet.Address, owner.Address))
            return null;
        if (action is null)
            return signing.Kind == "Workflow" || signing.Action is not null ? null : SigningAuthorityFence.Create(signing, [scope], prefix);
        // An action-bound operation signs only under its own frozen node witness, which selected both payer scopes.
        if (directOwnerOnly || signing.Kind != "Workflow" || signing.Action is not { } node
            || node.RunId.ToString("N") != action.RunId || node.NodeId.ToString("N") != action.NodeId || node.IntentHash != action.IntentHash)
            return null;
        return SigningAuthorityFence.Create(signing, WorkflowScopes, prefix);
    }

    private static IReadOnlyList<SurrealQuery>? Consent(WttOperationIntent intent, WttStageAuthority authority, string scope, string prefix,
        bool allowDirectOwner)
    {
        if (authority.Scope != scope || authority.Signing is not null || authority.Consent is not { } consent) return null;
        if (consent.Credential.ActorUserId != intent.ActorUserId
            || consent.Credential.TenantId != intent.TenantId && !(allowDirectOwner && consent.Credential.TenantId is null))
            return null;
        return SigningAuthorityFence.ForConsent(consent, [scope], prefix);
    }
}
