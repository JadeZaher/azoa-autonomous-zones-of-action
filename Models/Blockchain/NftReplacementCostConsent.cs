using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>Explicit replacement-generation acceptance of one immutable provider cost offer.</summary>
public sealed record NftReplacementCostAcceptance(NftStageAcceptance Stage, NftProtocolCostAuthorization Costs);

/// <summary>Separate replacement cost authority preserving the original command and all prior quotes.</summary>
public sealed record NftReplacementCostConsent(NftReplacementCostAcceptance Request, NftProtocolCostQuote Quote,
    SigningAdmissionAuthority Authority, DateTimeOffset AcceptedAt);

/// <summary>Durable version retirement preventing a delayed replacement acceptance from committing.</summary>
public sealed record NftReplacementCostRetirement(NftReplacementCostAcceptance Request, SigningAdmissionAuthority Authority,
    DateTimeOffset RetiredAt);

/// <summary>Ordered acceptance evidence distinguishing a committed request from a permanently obsolete version.</summary>
public sealed record NftReplacementCostReconciliation(string Status, AZOA.WebAPI.Models.Responses.NftLifecycleView Lifecycle);
