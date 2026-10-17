using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Helpers;

/// <summary>Hashes every immutable fee term without including the derived quote identifier.</summary>
public static class NftFeeQuoteIdentity
{
    public static string Compute(NftFeeQuote quote) => NftLifecycleIdentity.Fingerprint(JsonSerializer.Serialize(new
    {
        quote.ChainType, quote.Network, quote.Operation, quote.Revision, quote.Currency,
        quote.Decimals, quote.NativeBaseUnits, quote.TreasuryAddress, quote.PayerUserId,
    }));
}
