using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>The paying participant's explicit acceptance of one immutable cost offer for the exact reviewed stage.</summary>
public sealed record NftStageCostAcceptance(NftStageAcceptance Stage, NftProtocolCostAuthorization Costs);

/// <summary>Append-only stage cost authority; the latest entry for the current stage and generation governs signing.</summary>
public sealed record NftStageCostConsent(NftStageCostAcceptance Request, NftProtocolCostQuote Quote,
    SigningAdmissionAuthority Authority, DateTimeOffset AcceptedAt);
