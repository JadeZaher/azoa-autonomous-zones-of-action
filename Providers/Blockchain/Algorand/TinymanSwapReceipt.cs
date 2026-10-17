using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using AZOA.WebAPI.Core.Blockchain;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Verifies exact outer group and inner settlement evidence in a genesis-bound finalized block.</summary>
internal static class TinymanSwapReceipt
{
    internal static bool TryRead(TinymanWireParameters wire, IReadOnlyList<string> transactionIds,
        JsonElement block, out SwapStageObservation? observation)
    {
        observation = null;
        var expected = TinymanSwapWire.Build(wire);
        if (!expected.Select(tx => tx.TxID()).SequenceEqual(transactionIds) ||
            !Number(block, "round", out var round) || round < wire.FirstValid || round > wire.LastValid ||
            !Text(block, "genesis-hash", wire.GenesisHash) ||
            (wire.GenesisId.Length != 0 && !Text(block, "genesis-id", wire.GenesisId)) ||
            !Array(block, "transactions", out var all)) return false;
        var transactions = all.EnumerateArray().ToArray();
        var group = expected[0].Group is null ? null : Convert.ToBase64String(expected[0].Group.Bytes);
        var positions = new List<int>();
        for (var index = 0; index < transactions.Length; index++)
        {
            var tx = transactions[index];
            if (tx.ValueKind != JsonValueKind.Object) return false;
            if (tx.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && transactionIds.Contains(id.GetString()!))
                positions.Add(index);
            else if (group is not null && Text(tx, "group", group)) return false;
        }
        if (positions.Count != expected.Count || positions.Where((position, index) => position != positions[0] + index).Any()) return false;
        ulong lastOffset = 0;
        ulong networkFee = 0;
        for (var index = 0; index < expected.Count; index++)
        {
            var tx = transactions[positions[index]];
            var original = expected[index];
            if (!Text(tx, "id", transactionIds[index]) || !Text(tx, "sender", wire.Owner) ||
                !Number(tx, "confirmed-round", out var confirmed) || confirmed != round ||
                !Number(tx, "intra-round-offset", out var offset) || (index > 0 && offset <= lastOffset) ||
                !Number(tx, "fee", out var fee) || fee != original.Fee ||
                !Number(tx, "first-valid", out var first) || first != wire.FirstValid ||
                !Number(tx, "last-valid", out var last) || last != wire.LastValid ||
                !Text(tx, "note", Convert.ToBase64String(original.Note)) ||
                !AbsentOrEqual(tx, "genesis-hash", wire.GenesisHash) || !AbsentOrEqual(tx, "genesis-id", wire.GenesisId) ||
                !Absent(tx, "auth-addr", "rekey-to", "lease", "created-asset-index", "created-application-index") ||
                (group is null ? !Absent(tx, "group") : !Text(tx, "group", group))) return false;
            lastOffset = offset;
            networkFee = checked(networkFee + fee);
            if (wire.Stage == "OptIn")
            {
                if (!Transfer(tx, wire.OutputAsset, wire.Owner, 0) || !EmptyArray(tx, "inner-txns")) return false;
            }
            else if (wire.Stage == "CloseOptIn")
            {
                if (!Text(tx, "tx-type", "axfer") || !OnlyBody(tx, "asset-transfer-transaction") ||
                    !Object(tx, "asset-transfer-transaction", out var close) ||
                    !Number(close, "asset-id", out var asset) || asset != wire.OutputAsset ||
                    !Number(close, "amount", out var amount) || amount != 0 || !Text(close, "receiver", wire.Owner) ||
                    !Text(close, "close-to", wire.Owner) || !ZeroOrAbsent(close, "close-amount") ||
                    !Absent(close, "sender") || !EmptyArray(tx, "inner-txns")) return false;
            }
            else if (index == 1)
            {
                if (!Application(tx, (ApplicationCallTransaction)original)) return false;
            }
            else
            {
                var recipient = index == 0 ? TinymanV2PoolLocator.GetPoolAddress(wire.Validator, wire.InputAsset, wire.OutputAsset) : wire.Treasury;
                var amount = index == 0 ? wire.GrossInput - wire.PlatformFee : wire.PlatformFee;
                if (!Transfer(tx, wire.InputAsset, recipient, amount) || !EmptyArray(tx, "inner-txns")) return false;
            }
        }
        ulong output = 0;
        if (wire.Stage == "Swap")
        {
            var call = transactions[positions[1]];
            var pool = TinymanV2PoolLocator.GetPoolAddress(wire.Validator, wire.InputAsset, wire.OutputAsset);
            if (!Array(call, "inner-txns", out var inner) || inner.GetArrayLength() != 1) return false;
            var transfer = inner[0];
            var bodyName = wire.OutputAsset == 0 ? "payment-transaction" : "asset-transfer-transaction";
            if (!Text(transfer, "sender", pool) || !Number(transfer, "fee", out var innerFee) || innerFee != 0 ||
                !Object(transfer, bodyName, out var body) || !Number(body, "amount", out output) || output < wire.MinimumOutput ||
                !Transfer(transfer, wire.OutputAsset, wire.Owner, output) || !EmptyArray(transfer, "inner-txns") ||
                !Absent(transfer, "rekey-to", "created-asset-index", "created-application-index")) return false;
            if (!Logs(call, wire, output)) return false;
        }
        var evidence = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            format = "tinyman-v2-finalized-group/1", round = round.ToString(CultureInfo.InvariantCulture),
            genesisHash = wire.GenesisHash, transactionIds, group,
            blockDigest = CanonicalJson.Hash(block), firstOffset = transactions[positions[0]].GetProperty("intra-round-offset").GetUInt64().ToString(CultureInfo.InvariantCulture),
            output = output.ToString(CultureInfo.InvariantCulture),
        }));
        observation = new("Confirmed", transactionIds, "algorand:" + wire.GenesisHash,
            wire.Stage == "Swap" ? wire.GrossInput.ToString(CultureInfo.InvariantCulture) : "0", output.ToString(CultureInfo.InvariantCulture),
            wire.Stage == "Swap" ? wire.PlatformFee.ToString(CultureInfo.InvariantCulture) : "0",
            networkFee.ToString(CultureInfo.InvariantCulture), wire.Stage == "OptIn" ? null : "0", evidence);
        return true;
    }

    private static bool Application(JsonElement tx, ApplicationCallTransaction expected)
    {
        if (!Text(tx, "tx-type", "appl") || !OnlyBody(tx, "application-transaction") ||
            !Object(tx, "application-transaction", out var body) || !Number(body, "application-id", out var id) || id != expected.ApplicationId ||
            !Text(body, "on-completion", "noop") || !Array(body, "application-args", out var args) ||
            !Strings(args, expected.ApplicationArgs.Select(Convert.ToBase64String)) ||
            !Array(body, "accounts", out var accounts) || !Strings(accounts, expected.Accounts.Select(address => address.EncodeAsString())) ||
            !Array(body, "foreign-assets", out var assets) || assets.GetArrayLength() != expected.ForeignAssets.Count ||
            !EmptyArray(body, "foreign-apps") || !EmptyArray(body, "box-references") || !EmptyArray(body, "access") ||
            !Absent(body, "approval-program", "clear-state-program") || !EmptySchema(body, "local-state-schema") || !EmptySchema(body, "global-state-schema") ||
            !ZeroOrAbsent(body, "extra-program-pages") || !ZeroOrAbsent(body, "reject-version")) return false;
        var expectedAssets = expected.ForeignAssets.ToArray();
        var index = 0;
        foreach (var asset in assets.EnumerateArray())
            if (asset.ValueKind != JsonValueKind.Number || !asset.TryGetUInt64(out var value) || value != expectedAssets[index++]) return false;
        return true;
    }

    private static bool Logs(JsonElement call, TinymanWireParameters wire, ulong output)
    {
        string[] names = ["input_asset_id", "input_amount", "swap_amount", "change", "output_asset_id", "output_amount",
            "poolers_fee_amount", "protocol_fee_amount", "total_fee_amount"];
        if (!Array(call, "logs", out var logs) || logs.GetArrayLength() != names.Length) return false;
        var values = new ulong[names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            if (logs[index].ValueKind != JsonValueKind.String) return false;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(logs[index].GetString()!); }
            catch (FormatException) { return false; }
            var prefix = System.Text.Encoding.UTF8.GetBytes(names[index] + " %i");
            if (bytes.Length != prefix.Length + 8 || !bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix)) return false;
            values[index] = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(prefix.Length));
        }
        return values[0] == wire.InputAsset && values[1] == wire.GrossInput - wire.PlatformFee && values[3] == 0 &&
            values[4] == wire.OutputAsset && values[5] == output && values[8] > 0 &&
            (UInt128)values[2] + values[8] == values[1] && (UInt128)values[6] + values[7] == values[8];
    }

    private static bool Transfer(JsonElement tx, ulong asset, string recipient, ulong amount)
    {
        var bodyName = asset == 0 ? "payment-transaction" : "asset-transfer-transaction";
        if (!Text(tx, "tx-type", asset == 0 ? "pay" : "axfer") || !OnlyBody(tx, bodyName) ||
            !Object(tx, bodyName, out var body) || !Text(body, "receiver", recipient) ||
            !Number(body, "amount", out var actual) || actual != amount ||
            !Absent(body, "close-to", "close-remainder-to", "sender") || !ZeroOrAbsent(body, "close-amount") ||
            !ZeroOrAbsent(tx, "closing-amount")) return false;
        return asset == 0 || Number(body, "asset-id", out var id) && id == asset;
    }

    private static bool OnlyBody(JsonElement tx, string body)
        => tx.EnumerateObject().All(property => !property.Name.EndsWith("-transaction", StringComparison.Ordinal) || property.Name == body);
    private static bool Absent(JsonElement value, params string[] names) => names.All(name => !value.TryGetProperty(name, out _));
    private static bool AbsentOrEqual(JsonElement value, string name, string expected) => !value.TryGetProperty(name, out _) || Text(value, name, expected);
    private static bool ZeroOrAbsent(JsonElement value, string name) => !value.TryGetProperty(name, out _) || Number(value, name, out var number) && number == 0;
    private static bool EmptyArray(JsonElement value, string name) => !value.TryGetProperty(name, out _) || Array(value, name, out var items) && items.GetArrayLength() == 0;
    private static bool EmptySchema(JsonElement value, string name)
        => !value.TryGetProperty(name, out _) || Object(value, name, out var schema) &&
            Number(schema, "num-byte-slice", out var bytes) && bytes == 0 && Number(schema, "num-uint", out var integers) && integers == 0;
    private static bool Strings(JsonElement array, IEnumerable<string> expected)
        => array.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String) && array.EnumerateArray().Select(item => item.GetString()!).SequenceEqual(expected);
    internal static bool Number(JsonElement value, string name, out ulong number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number && property.TryGetUInt64(out number);
    }
    internal static bool Text(JsonElement value, string name, string expected)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() == expected;
    private static bool Object(JsonElement value, string name, out JsonElement property)
    {
        property = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out property) && property.ValueKind == JsonValueKind.Object;
    }
    private static bool Array(JsonElement value, string name, out JsonElement property)
    {
        property = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out property) && property.ValueKind == JsonValueKind.Array;
    }
}
