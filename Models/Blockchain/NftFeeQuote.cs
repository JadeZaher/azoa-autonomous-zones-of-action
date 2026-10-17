using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>Immutable platform-fee quote in the named chain's native atomic units; excludes network fees.</summary>
public sealed record NftFeeQuote(string ChainType, ChainNetwork Network, string Operation, long Revision,
    string Currency, int Decimals, string NativeBaseUnits, string TreasuryAddress, Guid PayerUserId)
{
    public string QuoteId => NftFeeQuoteIdentity.Compute(this);
}

/// <summary>Caller authorization for the exact quote identity, revision, and maximum native-unit spend.</summary>
public sealed record NftFeeAuthorization(long Revision, string MaxNativeBaseUnits, string QuoteId = "");

/// <summary>Provider-owned atomic fee leg signed by the original operation requester.</summary>
public sealed record NftFeeIntent(NftFeeQuote Quote, string PayerAddress, SigningContext SigningContext);
