using System.Text.Json;
using AZOA.WebAPI.Core.Blockchain;
using AZOA.WebAPI.Helpers;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Validated Tinyman V2 local pool state; network and validator bytecode are checked separately.</summary>
internal sealed record TinymanPoolState(string Address, ulong Validator, ulong Asset1, ulong Asset2,
    ulong Reserve1, ulong Reserve2, ulong ProtocolFees1, ulong ProtocolFees2,
    ulong TotalFeeShare, ulong ProtocolFeeRatio, ulong Round)
{
    internal bool TryQuote(ulong inputAsset, ulong input, out TinymanV2Math.Quote? quote)
    {
        quote = null;
        if (inputAsset != Asset1 && inputAsset != Asset2) return false;
        var first = inputAsset == Asset1;
        if (!TinymanV2Math.TryQuote(input, first ? Reserve1 : Reserve2, first ? Reserve2 : Reserve1,
            TotalFeeShare, ProtocolFeeRatio, out quote)) return false;
        if (quote!.ProtocolFee > ulong.MaxValue - (first ? ProtocolFees1 : ProtocolFees2))
        {
            quote = null;
            return false;
        }
        return true;
    }

    internal static bool TryRead(JsonElement account, ulong validator, ulong assetA, ulong assetB,
        out TinymanPoolState? state)
    {
        state = null;
        if (validator == 0 || assetA == assetB ||
            account.ValueKind != JsonValueKind.Object) return false;
        try
        {
            _ = CanonicalJson.Canonicalize(account);
            var address = TinymanV2PoolLocator.GetPoolAddress(validator, assetA, assetB);
            if (!Text(account, "address", out var actualAddress) || actualAddress != address ||
                !Text(account, "auth-addr", out var authority) || authority != global::Algorand.Address.ForApplication(validator).ToString() ||
                !Number(account, "round", out var round) || round == 0 ||
                !account.TryGetProperty("apps-local-state", out var applications) || applications.ValueKind != JsonValueKind.Array)
                return false;
            var matches = applications.EnumerateArray().Where(application => Number(application, "id", out var id) && id == validator).ToArray();
            if (matches.Length != 1 || !matches[0].TryGetProperty("key-value", out var values) ||
                values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 64) return false;
            var entries = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var value in values.EnumerateArray())
            {
                if (!Text(value, "key", out var encoded) || encoded.Length > 128 ||
                    !value.TryGetProperty("value", out var contents)) return false;
                var bytes = Convert.FromBase64String(encoded);
                if (Convert.ToBase64String(bytes) != encoded || bytes.Any(item => item > 127)) return false;
                var key = System.Text.Encoding.ASCII.GetString(bytes);
                if (!entries.TryAdd(key, contents)) return false;
            }
            string[] required = ["asset_1_id", "asset_2_id", "asset_1_reserves", "asset_2_reserves",
                "asset_1_protocol_fees", "asset_2_protocol_fees", "total_fee_share", "protocol_fee_ratio", "lock"];
            var parsed = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (var key in required)
            {
                if (!entries.TryGetValue(key, out var value) || !Number(value, "type", out var type) || type != 2 ||
                    !Number(value, "uint", out var number)) return false;
                parsed.Add(key, number);
            }
            if (parsed["asset_1_id"] != Math.Max(assetA, assetB) || parsed["asset_2_id"] != Math.Min(assetA, assetB) ||
                parsed["total_fee_share"] is < 1 or > 100 || parsed["protocol_fee_ratio"] is < 3 or > 10 || parsed["lock"] != 0)
                return false;
            state = new(address, validator, parsed["asset_1_id"], parsed["asset_2_id"], parsed["asset_1_reserves"],
                parsed["asset_2_reserves"], parsed["asset_1_protocol_fees"], parsed["asset_2_protocol_fees"],
                parsed["total_fee_share"], parsed["protocol_fee_ratio"], round);
            return true;
        }
        catch (JsonException) { return false; }
        catch (FormatException) { return false; }
    }

    private static bool Text(JsonElement value, string name, out string text)
    {
        text = string.Empty;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String) return false;
        text = property.GetString()!;
        return true;
    }

    private static bool Number(JsonElement value, string name, out ulong number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number && property.TryGetUInt64(out number);
    }
}
