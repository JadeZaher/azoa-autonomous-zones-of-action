using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Services.Users;

public sealed partial class NftLifecycleService
{
    private async Task<bool> CaptureStageConsentAsync(NftLifecycle row, NftLifecycleCommand command,
        Guid actor, Guid tenant, SigningCredentialWitness? credential, INftTransactionModule module, NftStageAcceptance? acceptance, CancellationToken ct)
    {
        if (acceptance is null || !NftStageConsentIdentity.Required(row, command)) return false;
        if (NftStageConsentIdentity.Current(row) is { } prior)
            return prior.Request == acceptance && NftStageConsentIdentity.Matches(row, command, prior);
        var recipient = NftStageConsentIdentity.RecipientSigns(row, command);
        if (actor != (recipient ? command.Recipient : command.Actor)) return false;
        var wallet = await wallets.GetByIdAsync(recipient ? command.RecipientWalletId : command.ActorWalletId, ct);
        if (wallet.IsError || wallet.Result is null
            || wallet.Result.Address != (recipient ? command.RecipientAddress : command.ActorAddress)
            || wallet.Result.ChainType != row.ChainType) return false;
        var authority = await BindActorAsync(actor, tenant, wallet.Result, row.Operation, credential, ct);
        if (authority is null) return false;
        var intent = NftLifecycleIntentFactory.Create(row, command, Enum.Parse<ChainNetwork>(row.Network),
            module.CredentialAuthorityAddress, tenant);
        if (intent is null) return false;
        var consent = new NftStageConsent(row.Id, row.Stage, row.PreparationGeneration,
            NftStageConsentIdentity.Hash(row, intent), authority, clock.GetUtcNow(), acceptance);
        row.StageConsentsJson = JsonSerializer.Serialize(NftStageConsentIdentity.Read(row).Append(consent));
        return NftStageConsentIdentity.Matches(row, command, consent);
    }

    /// <summary>Appends a fresher credential for the participant who already consented to this exact stage and generation.</summary>
    private async Task<bool> RenewStageConsentAsync(NftLifecycle row, NftLifecycleCommand command, Guid actor, Guid tenant,
        SigningCredentialWitness? credential, NftStageAcceptance acceptance, CancellationToken ct)
    {
        if (NftStageConsentIdentity.Current(row) is not { } held || held.Authority.Credential.ActorUserId != actor
            || (held.Authority.Credential.TenantId ?? Guid.Empty) != tenant) return false;
        var wallet = await wallets.GetByIdAsync(held.Authority.Wallet.Id, ct);
        if (wallet.IsError || wallet.Result is null || wallet.Result.Address != held.Authority.Wallet.Address
            || wallet.Result.ChainType != held.Authority.Wallet.ChainType) return false;
        var authority = await BindActorAsync(actor, tenant, wallet.Result, row.Operation, credential, ct);
        var intent = NftLifecycleIntentFactory.Create(row, command, Enum.Parse<ChainNetwork>(row.Network), command.PlatformAuthorityAddress, tenant);
        if (authority is null || intent is null || NftStageConsentIdentity.Hash(row, intent) != held.IntentHash) return false;
        var renewal = held with { Authority = authority, AcceptedAt = clock.GetUtcNow(), Request = acceptance };
        row.StageConsentsJson = JsonSerializer.Serialize(NftStageConsentIdentity.Read(row).Append(renewal));
        return NftStageConsentIdentity.Matches(row, command, renewal);
    }

    private async Task<SigningAdmissionAuthority?> BindActorAsync(Guid actor, Guid tenant, IWallet wallet,
        string operation, SigningCredentialWitness? credential, CancellationToken ct, ActionEffectAuthority? action = null)
    {
        if (action is not null)
        {
            if (action.ActorUserId != actor || (action.ActingTenantId ?? Guid.Empty) != tenant) return null;
            var loaded = await actionExecutions.GetByRunAndNodeAsync(action.RunId, action.NodeId, ct);
            if (loaded.IsError || loaded.Result is not { State: ActionNodeState.Running } execution
                || execution.RunId != action.RunId || execution.NodeId != action.NodeId
                || execution.SigningAuthorityJson is not { } signing || execution.FrozenInput is not { } frozen
                || CanonicalJson.HashCanonical(frozen) != action.IntentHash) return null;
            var witness = JsonSerializer.Deserialize<WorkflowSigningWitness>(signing);
            if (witness is null || witness.Action != action
                || !witness.RequiredScopes.Contains(NftSigningScope.ForOperation(operation), StringComparer.Ordinal)) return null;
            var original = await signingAuthorities.BindWorkflowAsync(witness, wallet.Id, wallet.ChainType, wallet.Address, ct);
            return original.IsError ? null : original.Result;
        }
        if (credential is null || credential.ActorUserId != actor || (credential.TenantId ?? Guid.Empty) != tenant) return null;
        var bound = await signingAuthorities.BindDirectAsync(credential, wallet.Id, wallet.ChainType, wallet.Address,
            [NftSigningScope.ForOperation(operation)], ct);
        return bound.IsError ? null : bound.Result;
    }
}
