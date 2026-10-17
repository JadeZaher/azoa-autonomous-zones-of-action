using System.Text.Json;
using AZOA.WebAPI.Models.Dex;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Reads immutable quote pins before refreshing a durable preparation.</summary>
internal sealed record JupiterAcceptedQuote(string Pool, bool SharedAccounts, bool WhirlpoolV2, bool AToB,
    string JupiterHash, string WhirlpoolHash, string GuardProgram, string GuardHash, string RentBase64,
    string ExecutionKey, string? NativeAccountKey, uint ComputeUnits, ulong ComputePrice, ulong TokenRent)
{
    internal static JupiterAcceptedQuote Read(DexQuoteTerms terms)
    {
        var root = terms.ProtocolEvidence;
        if (terms.Provider != "Jupiter" || terms.ProtocolVersion != "6-whirlpool/1" ||
            Text(root, "format") != "jupiter-whirlpool/1" || !root.TryGetProperty("guard", out var guard) ||
            !root.TryGetProperty("computeUnits", out var units) || units.ValueKind != JsonValueKind.Number ||
            !units.TryGetUInt32(out var computeUnits) || computeUnits is 0 or > 1_400_000 ||
            !UInt64Text.TryParse(Text(root, "computePrice"), out var computePrice) ||
            !UInt64Text.TryParse(Text(root, "rent"), out var tokenRent) || tokenRent == 0 ||
            !root.TryGetProperty("nativeAccountKey", out var nativeKey) ||
            nativeKey.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new InvalidDataException("The saved Jupiter quote lacks its immutable protocol and cost pins.");
        var key = nativeKey.ValueKind == JsonValueKind.String ? nativeKey.GetString() : null;
        var executionKey = Text(root, "executionKey");
        if (executionKey.Length > 512 || ((terms.InputAsset.Native || terms.OutputAsset.Native) ? key != executionKey : key is not null))
            throw new InvalidDataException("The saved Jupiter quote has an inconsistent native account identity.");
        return new(Text(root, "pool"), Flag(root, "sharedAccounts"), Flag(root, "whirlpoolV2"), Flag(root, "aToB"),
            Text(root, "JupiterHash"), Text(root, "WhirlpoolHash"), Text(guard, "Program"),
            Text(guard, "CodeHash"), Text(guard, "RentBase64"), executionKey, key, computeUnits, computePrice, tokenRent);
    }

    internal JupiterBuildPlan Bind(JupiterBuildPlan current, DexQuoteTerms original)
    {
        if (!UInt64Text.TryParse(original.SwapInput, out var input) ||
            !UInt64Text.TryParse(original.ExpectedOutput, out var output) ||
            !UInt64Text.TryParse(original.MinimumOutput, out var minimum) || original.SlippageBps is < 0 or >= 10000)
            throw new InvalidDataException("The saved Jupiter quote has invalid exact-input economics.");
        return JupiterAcceptedRoute.Bind(current, Pool, SharedAccounts, WhirlpoolV2, AToB,
            input, output, minimum, (ushort)original.SlippageBps);
    }

    private static string Text(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 } text
            ? text : throw new InvalidDataException("A saved Jupiter quote text pin is missing.");

    private static bool Flag(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean() : throw new InvalidDataException("A saved Jupiter quote route pin is missing.");
}
