using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Helpers;

/// <summary>Kinds of authority-bearing NFT lifecycle writes; see Providers/Stores/Surreal/AGENTS.md §NFT effect authorities.</summary>
public enum NftLifecycleWrite { FreshAdmission, Effect, CostAcceptance, RequesterRenewal, ParticipantRenewal }

/// <summary>One credential witness a lifecycle write must fence in its transaction.</summary>
public sealed record NftAuthorityRequirement(SigningAdmissionAuthority Authority, string Scope, string Prefix);

/// <summary>Selects the authorities each lifecycle write fences; shared by the durable store and its test double.</summary>
public static class NftEffectAuthorities
{
    /// <summary>Null fails closed: a required witness is missing or no longer binds this lifecycle.</summary>
    public static IReadOnlyList<NftAuthorityRequirement>? Select(NftLifecycle row, NftLifecycleCommand command, NftLifecycleWrite write)
    {
        var scope = NftSigningScope.ForOperation(row.Operation);
        if (write == NftLifecycleWrite.RequesterRenewal)
            return NftActorContinuationIdentity.Read(row).LastOrDefault() is { } renewal && NftActorContinuationIdentity.Matches(row, command, renewal)
                ? new[] { new NftAuthorityRequirement(renewal.Authority, scope, "nft_requester_renewal") } : null;
        if (write == NftLifecycleWrite.ParticipantRenewal)
            return NftStageConsentIdentity.Current(row) is { } renewed && NftStageConsentIdentity.Matches(row, command, renewed)
                ? new[] { new NftAuthorityRequirement(renewed.Authority, scope, "nft_stage_participant") } : null;
        if (write == NftLifecycleWrite.FreshAdmission
            && (NftCancellationTombstone.Matches(row, command) || NftCancellationTombstone.MatchesWorkflowRejection(row, command))) return Array.Empty<NftAuthorityRequirement>();
        var original = command.ActorAuthority;
        if (original is null || original.Credential.ActorUserId != command.Actor || original.Action != command.ActionAuthority
            || (original.Credential.TenantId ?? Guid.Empty) != command.ActingTenantId
            || original.Wallet.Id != command.ActorWalletId || original.Wallet.Address != command.ActorAddress
            || original.Wallet.ChainType != row.ChainType || row.ActorUserId != command.Actor.ToString("N")) return null;
        var requester = write == NftLifecycleWrite.FreshAdmission ? original : NftActorContinuationIdentity.Effective(row, command);
        if (requester is null) return null;
        var required = new List<NftAuthorityRequirement> { new(requester, scope, "nft_original_actor") };
        if (write == NftLifecycleWrite.FreshAdmission) return required;
        if (row.ChainType == "Solana" && row.Stage == NftTransactionKinds.Create && row.PreparationGeneration > 0 && row.ConfirmedStageCount == 0)
        {
            var costs = NftReplacementCostContract.Current(row);
            if (costs is null || !NftReplacementCostContract.Matches(row, command, costs)) return null;
            if (write == NftLifecycleWrite.CostAcceptance) required.Add(new(costs.Authority, AzoaScopes.NftMint, "nft_replacement_cost"));
        }
        if (NftStageConsentIdentity.Required(row, command))
        {
            var consent = NftStageConsentIdentity.Current(row);
            if (consent is null || !NftStageConsentIdentity.Matches(row, command, consent)) return null;
            required.Add(new(consent.Authority, scope, "nft_stage_participant"));
        }
        return required;
    }

    /// <summary>Mirrors the transaction fence's wall-clock expiry for direct witnesses; workflow witnesses carry none.</summary>
    public static bool Lapsed(SigningAdmissionAuthority authority, DateTimeOffset now)
        => authority.Kind is "DirectJwt" or "DirectApiKey" && authority.Credential.ExpiresAt is { } expires && expires <= now;
}
