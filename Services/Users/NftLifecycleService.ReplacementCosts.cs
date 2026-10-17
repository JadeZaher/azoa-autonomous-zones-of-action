using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Services.Users;

public sealed partial class NftLifecycleService
{
    /// <inheritdoc/>
    public async Task<AZOAResult<NftReplacementCostReconciliation>> ReconcileReplacementCostsAsync(string operationId,
        NftReplacementCostAcceptance request, SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (request?.Stage is null || request.Costs is null)
            return AZOAResult<NftReplacementCostReconciliation>.Failure("NFT replacement request is invalid.");
        var row = await store.GetAsync(operationId, ct);
        if (row is null || row.ActorUserId != credential.ActorUserId.ToString("N"))
            return AZOAResult<NftReplacementCostReconciliation>.Failure("NFT operation not found.");
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        if (command.ActingTenantId != (credential.TenantId ?? Guid.Empty) || request.Stage.RequestId == Guid.Empty
            || request.Stage.ExpectedVersion < 0 || request.Stage.ExpectedVersion == long.MaxValue)
            return AZOAResult<NftReplacementCostReconciliation>.Failure("NFT replacement request is invalid.");
        var accepted = NftReplacementCostContract.Read(row).SingleOrDefault(item => item.Request.Stage.RequestId == request.Stage.RequestId);
        if (accepted is not null) return accepted.Request == request
            ? AZOAResult<NftReplacementCostReconciliation>.Success(new("Applied", View(row)))
            : AZOAResult<NftReplacementCostReconciliation>.Failure("Replacement request ID is bound to another acceptance.");
        var retired = NftReplacementCostContract.Retirements(row).SingleOrDefault(item => item.Request.Stage.RequestId == request.Stage.RequestId);
        if (retired is not null && retired.Request != request)
            return AZOAResult<NftReplacementCostReconciliation>.Failure("Replacement request ID is bound to another retirement.");
        return AZOAResult<NftReplacementCostReconciliation>.Success(new(retired is not null || row.Version > request.Stage.ExpectedVersion
            ? "Obsolete" : "Unresolved", View(row)));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftReplacementCostReconciliation>> RetireReplacementCostsAsync(string operationId,
        NftReplacementCostAcceptance request, SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var outcome = await ReconcileReplacementCostsAsync(operationId, request, credential, ct);
        if (outcome.IsError || outcome.Result is not { Status: "Unresolved" }) return outcome;
        var row = await ReloadAsync(operationId, ct);
        if (!NftReplacementCostContract.Eligible(row) || !NftReplacementCostContract.Reviews(row, request.Stage))
            return await ReconcileReplacementCostsAsync(operationId, request, credential, ct);
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        var wallet = await wallets.GetByIdAsync(command.ActorWalletId, ct);
        if (wallet.IsError || wallet.Result is null || wallet.Result.Address != command.ActorAddress)
            return AZOAResult<NftReplacementCostReconciliation>.Failure("Original payer wallet is unavailable.");
        var authority = await BindActorAsync(command.Actor, command.ActingTenantId, wallet.Result, row.Operation, credential, ct);
        if (authority is null) return AZOAResult<NftReplacementCostReconciliation>.Failure("Current mint authority is required to retire this request.");
        row.ReplacementCostRetirementsJson = JsonSerializer.Serialize(NftReplacementCostContract.Retirements(row)
            .Append(new NftReplacementCostRetirement(request, authority, clock.GetUtcNow())));
        await SaveAsync(row, ct);
        return await ReconcileReplacementCostsAsync(operationId, request, credential, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftProtocolCostQuote>> QuoteReplacementCostsAsync(string operationId, NftStageAcceptance stage,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (stage is null) return CostRefused("Review the exact replacement generation before requesting costs.");
        var row = await store.GetAsync(operationId, ct);
        if (row is null || row.ActorUserId != credential.ActorUserId.ToString("N")) return CostRefused("NFT operation not found.");
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        var tenant = credential.TenantId ?? Guid.Empty;
        if (command.ActingTenantId != tenant) return CostRefused("NFT operation not found.");
        if (NftReplacementCostContract.Current(row) is { } existing) return AZOAResult<NftProtocolCostQuote>.Success(existing.Quote);
        if (!NftReplacementCostContract.Eligible(row) || !NftReplacementCostContract.Reviews(row, stage))
            return CostRefused("Review the exact proven-no-effect creation generation before requesting replacement costs.");
        var wallet = await wallets.GetByIdAsync(command.ActorWalletId, ct);
        if (wallet.IsError || wallet.Result is null || wallet.Result.Address != command.ActorAddress
            || await BindActorAsync(command.Actor, tenant, wallet.Result, row.Operation, credential, ct) is null)
            return CostRefused("The original payer wallet and current mint authority are required.");
        var accessResult = await access.RequireValueAccessAsync(command.Actor, credential.TenantId, ct);
        if (accessResult.IsError) return CostRefused(accessResult.Message);
        var allowed = await governance.EnsureAllowedAsync(row.ChainType, "NFT", "nft:mint", ct);
        if (allowed.IsRefused) return CostRefused(allowed.ConsumerMessage);
        var provider = providers.GetProvider(row.ChainType, Enum.Parse<ChainNetwork>(row.Network));
        if (!provider.TryGetModule<INftTransactionModule>(out var module) || module is null
            || !provider.TryGetModule<INftProtocolCostModule>(out var costs) || costs is null)
            return CostRefused("Verified NFT creation cost quotation is unavailable.");
        var intent = NftLifecycleIntentFactory.Create(row, command, provider.ActiveNetwork, module.CredentialAuthorityAddress, tenant);
        if (intent is null) return CostRefused("The original credential platform signer changed.");
        var estimate = await costs.QuoteNftProtocolCostsAsync(intent with { ProtocolCostQuote = null }, ct);
        if (estimate.IsError || estimate.Result is null) return CostRefused(estimate.Message);
        var now = clock.GetUtcNow();
        var quote = new NftProtocolCostQuote(Guid.NewGuid().ToString("N"), command.Actor, tenant, command.ActorWalletId,
            command.ActorAddress, row.ChainType, provider.ActiveNetwork, estimate.Result.PhysicalNetworkIdentity, row.Id,
            NftProtocolCostContract.DraftHash(row.Id, command.Actor, command.Recipient, command.Metadata), row.Stage,
            row.PreparationGeneration, estimate.Result.TokenStandard, estimate.Result.PolicyEvidenceJson, estimate.Result.Limits,
            now, now.AddMinutes(5), NftProtocolCostContract.IntentHash(intent, command.RecipientWalletId));
        if (!NftProtocolCostContract.IsValid(quote) || !NftReplacementCostContract.SamePhysicalNetwork(row, quote))
            return CostRefused("Replacement quotation does not match the original physical network.");
        var saved = await protocolCosts.ClaimAsync(quote, ct);
        return saved is null ? CostRefused("The replacement offer could not be recorded.") : AZOAResult<NftProtocolCostQuote>.Success(saved);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftLifecycleView>> AcceptReplacementCostsAsync(string operationId, NftReplacementCostAcceptance request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (request?.Stage is null || request.Costs is null || request.Stage.RequestId == Guid.Empty)
            return Refused("Exact replacement cost acceptance is required.");
        var row = await store.GetAsync(operationId, ct);
        if (row is null || row.ActorUserId != credential.ActorUserId.ToString("N")) return Refused("NFT operation not found.");
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        var tenant = credential.TenantId ?? Guid.Empty;
        if (tenant != command.ActingTenantId) return Refused("NFT operation not found.");
        var old = NftReplacementCostContract.Read(row);
        var prior = old.SingleOrDefault(item => item.Request.Stage.RequestId == request.Stage.RequestId);
        if (prior is not null) return prior.Request == request ? AZOAResult<NftLifecycleView>.Success(View(row))
            : Refused("The replacement acceptance key belongs to a different request.");
        if (!NftReplacementCostContract.Eligible(row) || !NftReplacementCostContract.Reviews(row, request.Stage)
            || NftReplacementCostContract.Current(row) is not null)
            return Refused("Review the current replacement generation before accepting costs.");
        var quote = await protocolCosts.GetAsync(request.Costs.QuoteId, command.Actor, tenant, ct);
        if (quote is null || !NftProtocolCostContract.IsAuthorized(quote, request.Costs)) return Refused("The exact replacement cost offer is unavailable.");
        var wallet = await wallets.GetByIdAsync(command.ActorWalletId, ct);
        if (wallet.IsError || wallet.Result is null || wallet.Result.Address != command.ActorAddress) return Refused("The original payer wallet changed.");
        var authority = await BindActorAsync(command.Actor, tenant, wallet.Result, row.Operation, credential, ct);
        if (authority is null) return Refused("Current mint authority is required to accept replacement costs.");
        var consent = new NftReplacementCostConsent(request, quote, authority, clock.GetUtcNow());
        row.ReplacementCostConsentsJson = JsonSerializer.Serialize(old.Append(consent));
        if (!NftReplacementCostContract.Matches(row, command, consent)) return Refused("Replacement costs do not match the original intent.");
        var accepted = await SaveAsync(row, ct, requireActionAuthority: true);
        if (accepted is not null) return AZOAResult<NftLifecycleView>.Success(View(accepted));
        var current = await ReloadAsync(row.Id, ct);
        var winner = NftReplacementCostContract.Read(current).SingleOrDefault(item => item.Request.Stage.RequestId == request.Stage.RequestId);
        return winner?.Request == request ? AZOAResult<NftLifecycleView>.Success(View(current))
            : Refused("Replacement acceptance did not commit; reconcile the saved request before replacing it.");
    }
}
