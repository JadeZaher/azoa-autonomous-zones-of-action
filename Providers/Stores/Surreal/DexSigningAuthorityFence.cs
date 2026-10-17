using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Binds shared signing authority to the exact immutable swap participants and workflow.</summary>
internal static class DexSigningAuthorityFence
{
    internal static IReadOnlyList<SurrealQuery>? Create(SwapOperationRecord operation, DexQuoteTerms terms,
        SigningAdmissionAuthority authority)
    {
        if (authority.Credential.ActorUserId != terms.ActorUserId || authority.Credential.TenantId != terms.TenantId ||
            terms.Owner is null || terms.Payer != terms.Owner || terms.Owner.UserId != terms.ActorUserId ||
            authority.Wallet.Id != terms.Owner.WalletId || authority.Wallet.OwnerUserId != terms.ActorUserId ||
            !string.Equals(authority.Wallet.ChainType, terms.Network.ChainType, StringComparison.OrdinalIgnoreCase) ||
            !ChainAddressIdentity.Equivalent(terms.Network.ChainType, authority.Wallet.Address, terms.Owner.Address))
            return null;
        if (operation.ActionRunId is null)
        {
            if (authority.Kind == "Workflow" || authority.Action is not null) return null;
        }
        else if (authority.Kind != "Workflow" || authority.Action is not { } action ||
            action.RunId.ToString("N") != operation.ActionRunId || action.NodeId.ToString("N") != operation.ActionNodeId ||
            action.IntentHash != operation.ActionIntentHash)
            return null;
        return SigningAuthorityFence.Create(authority, [AzoaScopes.SwapSign], "dex_signing");
    }

    internal static IReadOnlyList<SurrealQuery>? Cancel(DexQuoteTerms terms, SigningConsentWitness authority)
    {
        if (authority.Credential.ActorUserId != terms.ActorUserId ||
            (authority.Credential.TenantId is not null && authority.Credential.TenantId != terms.TenantId))
            return null;
        return SigningAuthorityFence.ForConsent(authority, [AzoaScopes.SwapSign], "dex_cancel");
    }

    internal static IReadOnlyList<SurrealQuery>? Cleanup(DexQuoteTerms terms, SwapCleanupConsent consent,
        SigningAdmissionAuthority authority)
    {
        if (authority.Kind == "Workflow" || authority.Action is not null ||
            authority.Credential.ActorUserId != consent.ActorUserId || authority.Credential.TenantId != consent.TenantId ||
            authority.Wallet.Id != terms.Owner.WalletId || authority.Wallet.OwnerUserId != terms.Owner.UserId ||
            !string.Equals(authority.Wallet.ChainType, terms.Network.ChainType, StringComparison.OrdinalIgnoreCase) ||
            !ChainAddressIdentity.Equivalent(terms.Network.ChainType, authority.Wallet.Address, terms.Owner.Address)) return null;
        return SigningAuthorityFence.Create(authority, [AzoaScopes.SwapSign], "dex_cleanup");
    }
}
