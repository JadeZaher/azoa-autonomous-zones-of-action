using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Exact target inner effects after saved-group and execution-position program proof.</summary>
internal static class WttAlgorandTargetEffects
{
    internal static ulong Read(WttOperationIntent intent, WttAlgorandTargetPlan plan, string stage,
        ulong asset, IReadOnlyList<JsonElement> transactions)
    {
        if (transactions.Count == 0) throw new InvalidDataException("Target finalized group is absent.");
        for (var index = 0; index < transactions.Count - 1; index++) RequireNoInner(transactions[index]);
        var root = transactions[^1];
        if (stage is not ("Register" or "Redeem")) { RequireNoInner(root); return asset; }
        if (!root.TryGetProperty("inner-txns", out var children) || children.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Target settlement omitted its actual inner effects.");
        if (stage == "Register")
        {
            if (plan.Attestation is null || plan.Asset is not null || children.GetArrayLength() != 1)
                throw new InvalidDataException("Initial registration must create exactly one wrapped asset.");
            var child = children[0]; Common(child, plan.CustodyAddress);
            if (Text(child, "tx-type") != "acfg" || !child.TryGetProperty("asset-config-transaction", out var config)
                || Number(child, "created-asset-index") is not { } created || created == 0
                || config.ValueKind != JsonValueKind.Object || OptionalNumber(config, "asset-id") != 0
                || !config.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Registration did not create its exact wrapped representation.");
            var fields = parameters.EnumerateObject().ToDictionary(value => value.Name, value => value.Value.Clone(), StringComparer.Ordinal);
            if (fields.TryGetValue("creator", out var creator) && creator.GetString() != plan.CustodyAddress)
                throw new InvalidDataException("Wrapped asset creator differs from protocol custody.");
            fields["creator"] = JsonSerializer.SerializeToElement(plan.CustodyAddress);
            var wrapped = WttAlgorandAsset.Read(JsonSerializer.SerializeToElement(new { index = created, @params = fields }), created);
            var metadata = WttPayloadCodec.ParseAttestation(Convert.FromHexString(plan.Attestation.Vaa.PayloadHex));
            var application = global::Algorand.Address.ForApplication(plan.TokenApplication).ToString();
            if (wrapped.Decimals != Math.Min(metadata.Decimals, (byte)8) || wrapped.Total != 18446744073709550000UL
                || wrapped.DefaultFrozen || wrapped.Manager != application || wrapped.Freeze != application || wrapped.Clawback != application
                || wrapped.Reserve != plan.CustodyAddress || wrapped.SymbolHex != TrimMetadata(metadata.SymbolHex, 8)
                || wrapped.NameHex != TrimMetadata(metadata.NameHex, 32))
                throw new InvalidDataException("Wrapped creation changed protocol supply, authority or metadata.");
            return created;
        }
        var fee = UInt64Text.Parse(WttPayloadCodec.Denormalize(intent.Amount.Fee, plan.Decimals));
        var recipient = UInt64Text.Parse(WttPayloadCodec.Denormalize(intent.Amount.RecipientAmount, plan.Decimals));
        if (children.GetArrayLength() != (fee == 0 ? 1 : 2))
            throw new InvalidDataException("Redemption contains missing or extra token effects.");
        Transfer(children[0], intent.RecipientAddress, recipient);
        if (fee != 0) Transfer(children[1], intent.TargetAuthority.Address, fee);
        return asset;

        void Transfer(JsonElement child, string recipientAddress, ulong amount)
        {
            Common(child, plan.CustodyAddress);
            var field = asset == 0 ? "payment-transaction" : "asset-transfer-transaction";
            if (Text(child, "tx-type") != (asset == 0 ? "pay" : "axfer") || !child.TryGetProperty(field, out var transfer)
                || transfer.ValueKind != JsonValueKind.Object || Text(transfer, "receiver") != recipientAddress
                || OptionalNumber(transfer, "amount") != amount || OptionalNumber(transfer, "close-amount") != 0
                || transfer.TryGetProperty("close-remainder-to", out _) || transfer.TryGetProperty("close-to", out _)
                || transfer.TryGetProperty("sender", out _) || asset != 0 && Number(transfer, "asset-id") != asset)
                throw new InvalidDataException("Redemption changed exact asset, destination, amount or close semantics.");
        }
    }

    private static void Common(JsonElement transaction, string sender)
    {
        _ = CanonicalJson.Canonicalize(transaction);
        if (Text(transaction, "sender") != sender || OptionalNumber(transaction, "fee") != 0
            || transaction.TryGetProperty("rekey-to", out _) || transaction.TryGetProperty("lease", out _)
            || transaction.TryGetProperty("note", out _) || OptionalNumber(transaction, "close-rewards") != 0)
            throw new InvalidDataException("Target inner transaction changed sender, fee or authority.");
        RequireNoInner(transaction);
    }
    private static void RequireNoInner(JsonElement transaction)
    {
        if (transaction.TryGetProperty("inner-txns", out var children)
            && (children.ValueKind != JsonValueKind.Array || children.GetArrayLength() != 0))
            throw new InvalidDataException("Target group contains an unexpected nested effect.");
    }
    private static string? Text(JsonElement value, string field)
        => value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static ulong? Number(JsonElement value, string field)
        => value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetUInt64(out var number) ? number : null;
    private static ulong? OptionalNumber(JsonElement value, string field) => value.TryGetProperty(field, out _) ? Number(value, field) : 0;
    private static string TrimMetadata(string encoded, int maximum)
    {
        var bytes = Convert.FromHexString(encoded); var firstZero = Array.IndexOf(bytes, (byte)0);
        var count = Math.Min(maximum, firstZero < 0 ? bytes.Length : firstZero);
        var padded = new byte[32]; bytes.AsSpan(0, count).CopyTo(padded);
        return Convert.ToHexStringLower(padded);
    }
}
