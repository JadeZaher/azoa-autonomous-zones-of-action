using System.Text.Json;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

internal sealed record JupiterInstructionAccount(string Address, bool Signer, bool Writable);
internal sealed record JupiterInstruction(string Program, IReadOnlyList<JupiterInstructionAccount> Accounts, byte[] Data);
internal sealed record JupiterBuildPlan(string InputMint, string OutputMint, ulong InputAmount, ulong OutputAmount,
    ulong MinimumOutput, string Pool, JupiterRouteData Route, JupiterInstruction Swap,
    IReadOnlyList<JupiterInstruction> Setup, IReadOnlyList<JupiterInstruction> Compute,
    IReadOnlyDictionary<string, IReadOnlyList<string>> LookupAddresses);

/// <summary>Reads the bounded official Build response without trusting its instructions or lookup addresses.</summary>
internal static class JupiterBuildPlanCodec
{
    internal static JupiterBuildPlan Read(JsonElement root, string inputMint, string outputMint, ulong inputAmount, int slippageBps)
    {
        if (root.ValueKind != JsonValueKind.Object || root.GetRawText().Length > 131072)
            throw new InvalidDataException("Jupiter Build response exceeds its bounded object shape.");
        CanonicalJson.Canonicalize(root);
        if (root.EnumerateObject().Any(property => property.Name is not ("inputMint" or "outputMint" or "inAmount" or "outAmount" or
            "otherAmountThreshold" or "swapMode" or "slippageBps" or "priceImpactPct" or "routePlan" or "computeBudgetInstructions" or
            "setupInstructions" or "swapInstruction" or "cleanupInstruction" or "otherInstructions" or "tipInstruction" or
            "addressesByLookupTableAddress" or "transactionVersion" or "computeUnitPrice" or "blockhashWithMetadata" or "platformFee")))
            throw new InvalidDataException("Jupiter Build contains unreviewed response semantics.");
        if (Text(root, "inputMint") != inputMint || Text(root, "outputMint") != outputMint ||
            Amount(root, "inAmount") != inputAmount || Text(root, "swapMode") != "ExactIn" ||
            !root.GetProperty("slippageBps").TryGetInt32(out var slippage) || slippage != slippageBps || slippage is < 0 or >= 10000 ||
            root.TryGetProperty("transactionVersion", out var version) && (!version.TryGetInt32(out var number) || number != 0))
            throw new InvalidDataException("Jupiter Build changed the exact requested economic mode.");
        if (root.TryGetProperty("computeUnitPrice", out var price) && price.ValueKind != JsonValueKind.Null ||
            NonNull(root, "tipInstruction") || NonNull(root, "cleanupInstruction") ||
            root.TryGetProperty("platformFee", out var fee) && fee.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Jupiter Build returned unsupported fee, tip, cleanup or v1 semantics.");
        var output = Amount(root, "outAmount");
        var minimum = Amount(root, "otherAmountThreshold");
        var routes = Array(root, "routePlan", 1);
        if (routes.Length != 1 || !routes[0].GetProperty("percent").TryGetInt32(out var percent) || percent != 100 ||
            !routes[0].GetProperty("bps").TryGetInt32(out var bps) || bps != 10000)
            throw new InvalidDataException("Jupiter Build must contain one complete unsplit route.");
        var hop = routes[0].GetProperty("swapInfo");
        if (Text(hop, "label") != "Whirlpool" || Text(hop, "inputMint") != inputMint || Text(hop, "outputMint") != outputMint ||
            Amount(hop, "inAmount") != inputAmount || Amount(hop, "outAmount") != output)
            throw new InvalidDataException("Jupiter route metadata differs from its exact Whirlpool hop.");
        var pool = Address(Text(hop, "ammKey"));
        var instruction = Instruction(root.GetProperty("swapInstruction"));
        if (instruction.Program != JupiterRouteAccounts.Program.Key || !JupiterRouteCodec.TryDecode(instruction.Data, out var route) ||
            route!.InputAmount != inputAmount || route.QuotedOutput != output || route.SlippageBps != slippage || route.MinimumOutput != minimum)
            throw new InvalidDataException("Jupiter route bytes differ from the advertised exact quote.");
        if (Array(root, "otherInstructions", 0).Length != 0)
            throw new InvalidDataException("Jupiter Build contains unrecognized auxiliary instructions.");
        var setup = Array(root, "setupInstructions", 4).Select(Instruction).ToArray();
        var compute = Array(root, "computeBudgetInstructions", 2).Select(Instruction).ToArray();
        var tables = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (root.TryGetProperty("addressesByLookupTableAddress", out var lookups) && lookups.ValueKind != JsonValueKind.Null)
        {
            if (lookups.ValueKind != JsonValueKind.Object || lookups.EnumerateObject().Count() > 8)
                throw new InvalidDataException("Jupiter lookup map exceeds its bound.");
            foreach (var table in lookups.EnumerateObject())
            {
                if (table.Value.ValueKind != JsonValueKind.Array || table.Value.GetArrayLength() > 256)
                    throw new InvalidDataException("Jupiter lookup table address list exceeds its bound.");
                var addresses = table.Value.EnumerateArray().Select(value => Address(value.GetString()!)).ToArray();
                if (addresses.Distinct(StringComparer.Ordinal).Count() != addresses.Length)
                    throw new InvalidDataException("Jupiter lookup map contains duplicate addresses.");
                tables.Add(Address(table.Name), addresses);
            }
        }
        return new(inputMint, outputMint, inputAmount, output, minimum, pool, route, instruction, setup, compute, tables);
    }

    private static JupiterInstruction Instruction(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(item => item.Name is not ("programId" or "accounts" or "data")))
            throw new InvalidDataException("Jupiter instruction has unknown fields.");
        var accounts = Array(value, "accounts", 64).Select(account =>
        {
            if (account.ValueKind != JsonValueKind.Object || account.EnumerateObject().Any(item => item.Name is not ("pubkey" or "isWritable" or "isSigner")) ||
                account.GetProperty("isWritable").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                account.GetProperty("isSigner").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Jupiter instruction account flags are not canonical.");
            return new JupiterInstructionAccount(Address(Text(account, "pubkey")), account.GetProperty("isSigner").GetBoolean(),
                account.GetProperty("isWritable").GetBoolean());
        }).ToArray();
        var encoded = Text(value, "data");
        if (encoded.Length > 1024) throw new InvalidDataException("Jupiter instruction payload exceeds its bound.");
        var data = Convert.FromBase64String(encoded);
        if (data.Length > 512 || Convert.ToBase64String(data) != encoded)
            throw new InvalidDataException("Jupiter instruction payload is not canonical base64.");
        return new(Address(Text(value, "programId")), accounts, data);
    }

    private static JsonElement[] Array(JsonElement root, string name, int maximum)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum)
            throw new InvalidDataException("Jupiter response array is missing or exceeds its bound.");
        return value.EnumerateArray().ToArray();
    }

    private static bool NonNull(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null;
    private static string Text(JsonElement root, string name) => root.GetProperty(name).GetString()
        ?? throw new InvalidDataException("Jupiter response text is missing.");
    private static ulong Amount(JsonElement root, string name) => UInt64Text.TryParse(Text(root, name), out var amount) && amount > 0
        ? amount : throw new InvalidDataException("Jupiter amount is not a positive canonical uint64 string.");
    private static string Address(string value) => value is { Length: >= 32 and <= 44 } && new PublicKey(value).Key == value
        ? value : throw new InvalidDataException("Jupiter account address is not canonical.");
}
