using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Users;

/// <summary>Resolves explicit native NFT fees independently of fungible split-fee schedules.</summary>
public static class NftNativeFeePolicy
{
    /// <summary>Quotes a configured operation in its provider's native atomic units.</summary>
    public static AZOAResult<NftFeeQuote> Quote(IConfiguration configuration, INftTransactionModule module,
        string chain, ChainNetwork network, string operation, Guid payer)
    {
        if (operation is not ("Mint" or "Transfer" or "Burn" or "Revoke" or "Refund"))
            return AZOAResult<NftFeeQuote>.Failure("Unknown NFT fee operation.");
        var entries = configuration.GetSection("NftFees:Policies").GetChildren()
            .Where(entry => string.Equals(entry["ChainType"], chain, StringComparison.OrdinalIgnoreCase)
                && entry["Network"] == network.ToString()).ToArray();
        if (entries.Length != 1) return AZOAResult<NftFeeQuote>.Failure("NFT native fee policy is unavailable or ambiguous.");
        var policy = entries[0];
        if (!long.TryParse(policy["Revision"], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision <= 0
            || !BigInteger.TryParse(policy[operation + "NativeBaseUnits"], NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || amount < 0 || module.NativeCurrencyDecimals is < 0 or > 36 || string.IsNullOrWhiteSpace(module.NativeCurrencyCode))
            return AZOAResult<NftFeeQuote>.Failure("NFT native fee policy is invalid.");
        var treasury = policy["TreasuryAddress"] ?? string.Empty;
        if (amount > 0 && string.IsNullOrWhiteSpace(treasury))
            return AZOAResult<NftFeeQuote>.Failure("NFT native fee treasury is unavailable.");
        return AZOAResult<NftFeeQuote>.Success(new(chain, network, operation, revision, module.NativeCurrencyCode,
            module.NativeCurrencyDecimals, amount.ToString(CultureInfo.InvariantCulture), treasury, payer));
    }

    /// <summary>Checks exact quoted revision and the caller's maximum before a durable intent is admitted.</summary>
    public static bool IsAuthorized(NftFeeQuote quote, NftFeeAuthorization? authorization)
    {
        if (quote.NativeBaseUnits == "0" && authorization is null) return true;
        return authorization is not null && authorization.Revision == quote.Revision && authorization.QuoteId == quote.QuoteId
            && BigInteger.TryParse(authorization.MaxNativeBaseUnits, NumberStyles.None, CultureInfo.InvariantCulture, out var maximum)
            && BigInteger.Parse(quote.NativeBaseUnits, CultureInfo.InvariantCulture) <= maximum;
    }
}
