using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Services.Hold;

public sealed partial class HoldCommandService
{
    /// <summary>Admits a frozen workflow command without signatures or undisclosed recipient costs.</summary>
    public async Task<AZOAResult<HoldCommandView>> AdmitWorkflowAsync(HoldCommandIntent intent,
        WorkflowSigningWitness witness, Guid executionId, Guid? recipientWalletId = null,
        HoldWorkflowMandateSelection? mandate = null, CancellationToken ct = default)
    {
        if (intent.Asset is null || executionId == Guid.Empty || witness.Action.ActorUserId != intent.ActorUserId
            || witness.Action.ActingTenantId != intent.TenantId || intent.ActorUserId != witness.Credential.ActorUserId
            || intent.TenantId != witness.Credential.TenantId) return Refuse();
        var id = HoldCommandIdentity.Operation(intent);
        var authority = new HoldCommandAuthority(1, Workflow: witness);
        var authorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority));
        var prior = await store.GetCommandAsync(id, ct);
        if (prior is not null)
        {
            if (prior.IntentHash != CanonicalJson.Hash(intent) || prior.AuthorityJson != authorityJson) return Refuse();
            var savedPlan = prior.RailPlanJson is null ? null : JsonSerializer.Deserialize<HoldRailPlan>(prior.RailPlanJson);
            var savedDraft = prior.WorkflowMandateDraftJson is null ? null : JsonSerializer.Deserialize<HoldWorkflowMandateDraft>(prior.WorkflowMandateDraftJson);
            if ((savedPlan?.RecipientWallet?.Id ?? savedDraft?.Terms.Recipient.Id) != recipientWalletId
                || (savedDraft is not null && (savedDraft.ExecutionRecordId != executionId.ToString("N")
                    || mandate is null || savedDraft.Terms.ExpiresAt != mandate.ExpiresAt
                    || savedDraft.Terms.MaximumCosts != mandate.MaximumCosts))) return Refuse();
            return await ReadResultAsync(prior, ct);
        }
        var ready = await valueAccess.RequireValueAccessAsync(intent.ActorUserId, intent.TenantId, ct);
        if (ready.IsError) return AZOAResult<HoldCommandView>.Failure(ready.Message);
        var current = await store.GetHoldAsync(HoldCommandIdentity.Hold(intent), ct);
        var local = intent.Kind == "ReleaseIntoHold" || (intent.Kind == "Open" && intent.Definition?.FundingSource == "Reserve")
            || (intent.Kind == "Refund" && current?.FundingSource == "Reserve");
        var automatic = intent.Definition?.AutoRelease == true || intent.NextAutoRelease;
        if (local && !automatic)
        {
            if (mandate is not null || recipientWalletId is not null) return Refuse();
            var committed = await store.CommitLocalAsync(intent, authority, ct);
            return committed is null ? AZOAResult<HoldCommandView>.Failure("The exact workflow source or custody liability changed.")
                : AZOAResult<HoldCommandView>.Success(JsonSerializer.Deserialize<HoldCommandView>(committed.OutcomeJson)!);
        }
        if (recipientWalletId is null || (!local && mandate is not null)) return Refuse();
        var provider = providers.GetProvider(intent.Asset.ChainType, intent.Asset.Network);
        if (!provider.TryGetModule<IValueCustodyModule>(out var custody)
            || !provider.TryGetModule<IValueTransferTransactionModule>(out _))
            return AZOAResult<HoldCommandView>.Failure("The selected network does not expose complete custody mechanics.");
        var beneficiary = intent.Kind == "ReleaseIntoHold" ? intent.NextBeneficiaryUserId
            : intent.Definition?.BeneficiaryUserId;
        HoldDefinition? definition = current?.DefinitionJson is null ? null : JsonSerializer.Deserialize<HoldDefinition>(current.DefinitionJson);
        if (!local) beneficiary = intent.Kind == "Release" ? definition?.BeneficiaryUserId : definition?.FunderUserId;
        var loaded = await wallets.GetByIdAsync(recipientWalletId.Value, ct);
        if (loaded.IsError || loaded.Result is not { } wallet || wallet.UserId != beneficiary
            || wallet.ChainType != intent.Asset.ChainType) return Refuse();
        var recipient = new SigningWalletWitness(wallet.Id, wallet.UserId, wallet.Address, wallet.ChainType);
        var quantity = intent.Definition?.QuantityBaseUnits ?? current?.Amount;
        if (quantity is null) return Refuse();
        var quote = await custody!.QuoteValueCustodyAsync(new(intent.Asset.ChainType, intent.Asset.Network, intent.Asset.AssetId,
            intent.Asset.HoldingAddress, recipient.Address, quantity, intent.Asset.HoldingAddress), ct);
        if (quote.IsError || quote.Result is null) return AZOAResult<HoldCommandView>.Failure(quote.Message);
        if (local)
        {
            if (mandate is null) return Refuse();
            var liability = intent.Kind == "Open" ? intent.Definition : definition is null ? null : definition with {
                BeneficiaryUserId = intent.NextBeneficiaryUserId!.Value, ReleaseNotBefore = intent.NextReleaseNotBefore, AutoRelease = true };
            if (liability is null) return Refuse();
            var terms = new HoldReleaseMandateTerms(1, HoldCommandIdentity.Hold(intent, intent.Kind == "Open" ? intent.HoldId : intent.NextHoldId),
                CanonicalJson.Hash(liability), intent.Asset, quantity, recipient, liability.ReleaseNotBefore ?? quote.Result.QuotedAt,
                mandate.ExpiresAt, quote.Result.NativeCurrency, quote.Result.NativeDecimals, mandate.MaximumCosts);
            var pending = await store.AdmitWorkflowOpeningAsync(intent, authority, new(1, executionId.ToString("N"), terms, quote.Result), ct);
            return pending is null ? AZOAResult<HoldCommandView>.Failure("The frozen mandate bounds or original execution no longer permit disclosure.") : Result(pending);
        }
        var kind = intent.Kind switch { "Release" => "Release", "Refund" => "FundedRefund", _ => null };
        if (kind is null) return Refuse();
        var signing = new SigningContext(Guid.Empty, Guid.Empty, true, intent.TenantId ?? Guid.Empty,
            intent.TenantId is null ? Guid.Empty : intent.ActorUserId, AzoaScopes.TransferSign, intent.Asset.ChainType);
        var plan = new HoldRailPlan(1, kind, id, 0, quote.Result, null, recipient, signing);
        var admitted = await store.ClaimRailAsync(intent, plan, authority, ct);
        return admitted is null ? AZOAResult<HoldCommandView>.Failure("The original workflow, recipient or custody liability changed.") : Result(admitted);
    }
}
