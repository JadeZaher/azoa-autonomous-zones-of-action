using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Helpers;

/// <summary>Validates immutable NFT cost receipts and non-interchangeable spending categories.</summary>
public static class NftProtocolCostContract
{
    public static string IntentHash(NftTransactionIntent intent, Guid recipientWalletId)
        => CanonicalJson.Hash(new { domain = "nft-protocol-cost-intent/1", recipientWalletId, intent = intent with { ProtocolCostQuote = null } });

    public static string DraftHash(string lifecycleId, Guid actor, Guid recipient, UserNFTMintModel request)
    {
        var metadata = JsonSerializer.SerializeToNode(request)!.AsObject();
        metadata.Remove(nameof(UserNFTMintModel.ProtocolCostAuthorization));
        return CanonicalJson.Hash(new { domain = "nft-protocol-cost-draft/1", lifecycleId, actor, recipient, metadata });
    }

    public static string Hash(NftProtocolCostQuote quote)
        => CanonicalJson.Hash(new { domain = "nft-protocol-cost/1", quote });

    public static bool IsValid(NftProtocolCostQuote? quote)
    {
        if (quote is null || !Guid.TryParseExact(quote.QuoteId, "N", out var id) || id == Guid.Empty ||
            quote.ActorUserId == Guid.Empty || quote.PayerWalletId == Guid.Empty ||
            string.IsNullOrWhiteSpace(quote.PayerAddress) || string.IsNullOrWhiteSpace(quote.ChainType) ||
            !Enum.IsDefined(quote.Network) || string.IsNullOrWhiteSpace(quote.PhysicalNetworkIdentity) ||
            string.IsNullOrWhiteSpace(quote.LifecycleId) || !IsHash(quote.DraftHash) ||
            string.IsNullOrWhiteSpace(quote.Stage) || quote.PreparationGeneration < 0 ||
            string.IsNullOrWhiteSpace(quote.TokenStandard) || quote.CreatedAt >= quote.ExpiresAt ||
            !ValidLimits(quote.Limits) || string.IsNullOrWhiteSpace(quote.PolicyEvidenceJson)
            || (quote.QuotedIntentHash is not null && !IsHash(quote.QuotedIntentHash))) return false;
        try
        {
            using var evidence = JsonDocument.Parse(quote.PolicyEvidenceJson);
            return evidence.RootElement.ValueKind == JsonValueKind.Object && evidence.RootElement.EnumerateObject().Any() &&
                CanonicalJson.Canonicalize(evidence.RootElement) == quote.PolicyEvidenceJson;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsAuthorized(NftProtocolCostQuote quote, NftProtocolCostAuthorization? authorization)
        => IsValid(quote) && authorization is not null && authorization.QuoteId == quote.QuoteId &&
            authorization.QuoteHash == Hash(quote) && authorization.Limits == quote.Limits;

    public static bool Covers(NftProtocolCostLimits? limits, NftProtocolCostLimits? costs)
        => ValidLimits(limits) && ValidLimits(costs) && Within(costs!.NetworkFee, limits!.NetworkFee) &&
            Within(costs.RetainedRent, limits.RetainedRent) && Within(costs.ProtocolLevy, limits.ProtocolLevy);

    private static bool ValidLimits(NftProtocolCostLimits? limits)
        => limits is not null && UnsignedIntegerText.TryParse(limits.NetworkFee, out _) &&
            UnsignedIntegerText.TryParse(limits.RetainedRent, out _) && UnsignedIntegerText.TryParse(limits.ProtocolLevy, out _);

    private static bool Within(string amount, string limit)
        => UnsignedIntegerText.TryParse(amount, out var value) && UnsignedIntegerText.TryParse(limit, out var cap) && value <= cap;

    private static bool IsHash(string? value)
        => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
}
