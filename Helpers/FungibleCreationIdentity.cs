using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Helpers;

/// <summary>Hashes immutable fungible intent, quote and provider preparation identities.</summary>
public static class FungibleCreationIdentity
{
    public static string OperationId(Guid actor, Guid? tenant, string key)
        => CanonicalJson.Hash(new { Domain = "fungible-create/1", Actor = actor, Tenant = tenant, Key = key });
    public static string IntentHash(FungibleCreationIntent intent) => CanonicalJson.Hash(intent with { Quote = null });
    public static string QuoteId(FungibleCreationQuote quote) => CanonicalJson.Hash(quote with { QuoteId = "" });
    public static string PreparationId(FungibleCreationIntent intent)
        => CanonicalJson.Hash(new { Domain = "fungible-create/1", intent.PreparationKey, intent.Generation, Hash = CanonicalJson.Hash(intent) });
    public static bool IsValidQuote(FungibleCreationIntent intent)
        => intent.Quote is { } quote && quote.QuoteId == QuoteId(quote) && quote.IntentHash == IntentHash(intent)
            && quote.PayerAddress == intent.CreatorAddress;
}
