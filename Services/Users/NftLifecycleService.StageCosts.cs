using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Users;

/// <summary>Recipient-stage Solana cost quote and acceptance (F-M11); see Services/Users/AGENTS.md §NFT recipient-stage costs.</summary>
public sealed partial class NftLifecycleService
{
    /// <inheritdoc/>
    public async Task<AZOAResult<NftProtocolCostQuote>> QuoteStageCostsAsync(string operationId, NftStageAcceptance stage,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (stage is null) return CostRefused("Review the exact NFT stage before requesting its costs.");
        var row = await store.GetAsync(operationId, ct);
        if (row is null) return CostRefused("NFT operation not found.");
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        var payer = await PayerAuthorityAsync(row, command, credential, ct);
        if (payer is null) return CostRefused("Only the participant who pays this NFT stage can review its costs.");
        if (!NftStageCostContract.Eligible(row, command) || !NftStageCostContract.Reviews(row, stage))
            return CostRefused("Review the current unsigned NFT stage before requesting its costs.");
        var tenant = payer.Value.Tenant;
        var accessResult = await access.RequireValueAccessAsync(credential.ActorUserId, tenant == Guid.Empty ? null : tenant, ct);
        if (accessResult.IsError) return CostRefused(accessResult.Message);
        var allowed = await governance.EnsureAllowedAsync(row.ChainType, "NFT", StageAction(row.Operation), ct);
        if (allowed.IsRefused) return CostRefused(allowed.ConsumerMessage);
        var provider = providers.GetProvider(row.ChainType, Enum.Parse<ChainNetwork>(row.Network));
        if (!provider.TryGetModule<INftTransactionModule>(out var module) || module is null
            || !provider.TryGetModule<INftProtocolCostModule>(out var costs) || costs is null)
            return CostRefused("Verified NFT stage cost quotation is unavailable.");
        var intent = NftLifecycleIntentFactory.Create(row, command, provider.ActiveNetwork, module.CredentialAuthorityAddress, tenant);
        if (intent is null) return CostRefused("The original credential platform signer changed.");
        var estimate = await costs.QuoteNftProtocolCostsAsync(intent with { ProtocolCostQuote = null }, ct);
        if (estimate.IsError || estimate.Result is null) return CostRefused(estimate.Message);
        var (user, walletId, address) = NftStageCostContract.Payer(row, command);
        var now = clock.GetUtcNow();
        var quote = new NftProtocolCostQuote(Guid.NewGuid().ToString("N"), user, tenant, walletId, address, row.ChainType,
            provider.ActiveNetwork, estimate.Result.PhysicalNetworkIdentity, row.Id, NftStageCostContract.DraftHash(row), row.Stage,
            row.PreparationGeneration, estimate.Result.TokenStandard, estimate.Result.PolicyEvidenceJson, estimate.Result.Limits,
            now, now.AddMinutes(5), NftProtocolCostContract.IntentHash(intent, command.RecipientWalletId));
        if (!NftProtocolCostContract.IsValid(quote) || (row.ProtocolIdentity is not null && row.ProtocolIdentity != quote.PhysicalNetworkIdentity))
            return CostRefused("Stage quotation does not match the asset's recorded network.");
        var saved = await protocolCosts.ClaimAsync(quote, ct);
        return saved is null ? CostRefused("The stage cost offer could not be recorded.") : AZOAResult<NftProtocolCostQuote>.Success(saved);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftLifecycleView>> AcceptStageCostsAsync(string operationId, NftStageCostAcceptance request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (request?.Stage is null || request.Costs is null || request.Stage.RequestId == Guid.Empty)
            return Refused("Exact NFT stage cost acceptance is required.");
        var row = await store.GetAsync(operationId, ct);
        if (row is null) return Refused("NFT operation not found.");
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        var old = NftStageCostContract.Read(row);
        var prior = old.SingleOrDefault(item => item.Request.Stage.RequestId == request.Stage.RequestId);
        if (prior is not null)
            return prior.Request == request && prior.Authority.Credential.ActorUserId == credential.ActorUserId
                ? AZOAResult<NftLifecycleView>.Success(View(row)) : Refused("The stage cost acceptance key belongs to a different request.");
        var payer = await PayerAuthorityAsync(row, command, credential, ct);
        if (payer is null) return Refused("Only the participant who pays this NFT stage can accept its costs.");
        if (!NftStageCostContract.Eligible(row, command) || !NftStageCostContract.Reviews(row, request.Stage))
            return Refused("Review the current unsigned NFT stage before accepting its costs.");
        var quote = await protocolCosts.GetAsync(request.Costs.QuoteId, credential.ActorUserId, payer.Value.Tenant, ct);
        if (quote is null || !NftProtocolCostContract.IsAuthorized(quote, request.Costs))
            return Refused("The exact NFT stage cost offer is unavailable.");
        var consent = new NftStageCostConsent(request, quote, payer.Value.Authority, clock.GetUtcNow());
        row.StageCostConsentsJson = JsonSerializer.Serialize(old.Append(consent));
        if (!NftStageCostContract.Matches(row, command, consent)) return Refused("The stage cost offer does not match this exact stage and payer.");
        var accepted = await SaveAsync(row, ct, requireActionAuthority: true);
        if (accepted is not null) return AZOAResult<NftLifecycleView>.Success(View(accepted));
        var current = await ReloadAsync(row.Id, ct);
        return NftStageCostContract.Read(current).Any(item => item.Request == request)
            ? AZOAResult<NftLifecycleView>.Success(View(current))
            : Refused("Stage cost acceptance did not commit: the offer expired or the stage changed; review a new quote.");
    }

    /// <summary>Binds the caller as the stage's fee payer under the tenant that payer must act in; null refuses.</summary>
    private async Task<(Guid Tenant, SigningAdmissionAuthority Authority)?> PayerAuthorityAsync(NftLifecycle row, NftLifecycleCommand command,
        SigningCredentialWitness credential, CancellationToken ct)
    {
        if (!NftStageCostContract.Required(row) || NftStageCostContract.PayerTenant(row, command) is not { } tenant) return null;
        var (user, walletId, address) = NftStageCostContract.Payer(row, command);
        if (credential.ActorUserId != user || (credential.TenantId ?? Guid.Empty) != tenant) return null;
        var wallet = await wallets.GetByIdAsync(walletId, ct);
        if (wallet.IsError || wallet.Result is null || wallet.Result.Address != address || wallet.Result.ChainType != row.ChainType) return null;
        var authority = await BindActorAsync(user, tenant, wallet.Result, row.Operation, credential, ct);
        return authority is null ? null : (tenant, authority);
    }

    /// <summary>Read-only re-quote before preparation; refuses when current costs exceed the accepted limits or the network moved.</summary>
    private async Task<AZOAResult<NftLifecycleView>?> StageCostsChangedAsync(IBlockchainProvider provider, NftTransactionIntent intent, CancellationToken ct)
    {
        var accepted = intent.ProtocolCostQuote;
        if (accepted is null || !provider.TryGetModule<INftProtocolCostModule>(out var costs) || costs is null)
            return Refused("This NFT stage requires the paying participant's explicitly accepted cost quote.");
        var fresh = await costs.QuoteNftProtocolCostsAsync(intent with { ProtocolCostQuote = null }, ct);
        if (fresh.IsError || fresh.Result is null) return Refused(fresh.Message);
        return fresh.Result.PhysicalNetworkIdentity == accepted.PhysicalNetworkIdentity && fresh.Result.TokenStandard == accepted.TokenStandard
            && NftProtocolCostContract.Covers(accepted.Limits, fresh.Result.Limits) ? null
            : AZOAResult<NftLifecycleView>.FailureWithCode("NFT stage costs changed since acceptance; review and accept a new quote.",
                "NFT_STAGE_COSTS_CHANGED");
    }

    private static string StageAction(string operation) => operation switch
    {
        "Mint" => "nft:mint", "Burn" => "nft:burn", "Revoke" => "credential:revoke", _ => "nft:transfer",
    };
}
