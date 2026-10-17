using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>Discloses and activates one exact delayed-release mandate under current actual owner authority.</summary>
public sealed partial class HoldReleaseMandateService(IHoldReleaseMandateStore mandates, IHoldCommandStore holds,
    SigningAdmissionAuthorityFactory authorities, IBlockchainProviderFactory providers, IValueAccessService access,
    HoldRailExecutor rail)
{
    public async Task<AZOAResult<HoldReleaseMandateOfferView>> OfferAsync(HoldReleaseMandateOfferRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (request?.Terms?.Asset is null || request.RequestId == Guid.Empty || request.HoldVersion < 0)
            return AZOAResult<HoldReleaseMandateOfferView>.Failure("Exact release terms and request identity are required.");
        var id = HoldReleaseMandateIdentity.Id(credential.ActorUserId, credential.TenantId, request.RequestId);
        var original = await mandates.GetOfferAsync(id, ct);
        if (original is not null)
            return original.InputHash == CanonicalJson.Hash(request) && original.ActorUserId == credential.ActorUserId.ToString("N")
                && original.TenantId == credential.TenantId?.ToString("N")
                ? AZOAResult<HoldReleaseMandateOfferView>.Success(new(id, original.RequestHash,
                    JsonSerializer.Deserialize<HoldReleaseMandateRequest>(original.RequestJson)!))
                : AZOAResult<HoldReleaseMandateOfferView>.Failure("This original mandate request has different immutable terms.");
        var hold = await holds.GetHoldAsync(request.Terms.HoldRecordId, ct);
        if (hold?.DefinitionJson is null || hold.OwnerUserId != credential.ActorUserId.ToString("N")
            || SurrealRecordGuid.ParseOptional(hold.TenantId) != credential.TenantId || hold.Version != request.HoldVersion
            || hold.State != "Held" || hold.ActiveCommandId is not null || hold.ActiveReleaseMandateId is not null
            || request.Terms.Asset != JsonSerializer.Deserialize<HoldDefinition>(hold.DefinitionJson)!.Asset
            || request.Terms.Recipient is null || request.Terms.Recipient.OwnerUserId != SurrealRecordGuid.Parse(hold.BeneficiaryUserId))
            return AZOAResult<HoldReleaseMandateOfferView>.Failure("The original owned custody liability or beneficiary changed.");
        var ready = await access.RequireValueAccessAsync(credential.ActorUserId, credential.TenantId, ct);
        if (ready.IsError) return AZOAResult<HoldReleaseMandateOfferView>.Failure(ready.Message);
        var authority = await authorities.BindConsentAsync(credential, [AzoaScopes.TransferSign], ct);
        if (authority.IsError || authority.Result is null) return AZOAResult<HoldReleaseMandateOfferView>.Failure(authority.Message);
        var provider = providers.GetProvider(request.Terms.Asset.ChainType, request.Terms.Asset.Network);
        if (!provider.TryGetModule<IValueCustodyModule>(out var custody))
            return AZOAResult<HoldReleaseMandateOfferView>.Failure("Current complete custody costs are unavailable for this network.");
        var quote = await custody!.QuoteValueCustodyAsync(new(hold.Chain, request.Terms.Asset.Network, hold.AssetId,
            hold.HoldingAddress, request.Terms.Recipient.Address, hold.Amount, hold.HoldingAddress), ct);
        if (quote.IsError || quote.Result is null) return AZOAResult<HoldReleaseMandateOfferView>.Failure(quote.Message);
        var saved = await mandates.OfferAsync(new(request.RequestId, request.HoldVersion, request.Terms, quote.Result), authority.Result, ct);
        return saved is null ? AZOAResult<HoldReleaseMandateOfferView>.Failure("The cost bounds, current authority or original liability changed.")
            : AZOAResult<HoldReleaseMandateOfferView>.Success(new(saved.Id, saved.RequestHash,
                JsonSerializer.Deserialize<HoldReleaseMandateRequest>(saved.RequestJson)!));
    }

    public async Task<AZOAResult<HoldReleaseMandateView>> AcceptAsync(HoldReleaseMandateAcceptance acceptance,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (acceptance is null || string.IsNullOrWhiteSpace(acceptance.OfferId) || string.IsNullOrWhiteSpace(acceptance.RequestHash))
            return AZOAResult<HoldReleaseMandateView>.Failure("The original disclosure identity is required.");
        var prior = await mandates.GetAsync(acceptance.OfferId, ct);
        if (prior is not null)
            return prior.ActorUserId == credential.ActorUserId.ToString("N") && prior.TenantId == credential.TenantId?.ToString("N")
                && prior.RequestHash == acceptance.RequestHash
                ? AZOAResult<HoldReleaseMandateView>.Success(View(prior))
                : AZOAResult<HoldReleaseMandateView>.Failure("This mandate belongs to another immutable request.");
        var authority = await authorities.BindConsentAsync(credential, [AzoaScopes.TransferSign], ct);
        if (authority.IsError || authority.Result is null) return AZOAResult<HoldReleaseMandateView>.Failure(authority.Message);
        var saved = await mandates.AdmitAsync(acceptance, authority.Result, ct);
        return saved is null ? AZOAResult<HoldReleaseMandateView>.Failure("The exact mandate disclosure or current owner authority changed.")
            : AZOAResult<HoldReleaseMandateView>.Success(View(saved));
    }
}
