using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using AZOA.WebAPI.Helpers;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

internal sealed record AlgorandConfirmedGroup(ulong Round, ulong Timestamp, ulong NetworkFee,
    IReadOnlyList<JsonElement> Transactions, string BlockDigest);

/// <summary>Checks every frozen outer transaction against its contiguous finalized, genesis-bound group.</summary>
internal static class AlgorandGroupReceipt
{
    internal static AlgorandConfirmedGroup? Read(IReadOnlyList<Transaction> expected, JsonElement block)
    {
        if (expected.Count is < 1 or > 16) return null;
        _ = CanonicalJson.Canonicalize(block);
        var first = expected[0];
        if (!Number(block, "round", out var round) || round < first.FirstValid || round > first.LastValid
            || !Number(block, "timestamp", out var timestamp) || !Text(block, "genesis-hash", Convert.ToBase64String(first.GenesisHash.Bytes))
            || !Text(block, "genesis-id", first.GenesisId) || !Array(block, "transactions", out var array)) return null;
        var ids = expected.Select(transaction => transaction.TxID()).ToArray();
        var group = Convert.ToBase64String(first.Group.Bytes);
        var selected = new List<JsonElement>(); var positions = new List<int>(); var position = 0;
        foreach (var transaction in array.EnumerateArray())
        {
            if (transaction.ValueKind != JsonValueKind.Object) return null;
            if (transaction.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && ids.Contains(id.GetString()!))
            { selected.Add(transaction.Clone()); positions.Add(position); }
            else if (Text(transaction, "group", group)) return null;
            position++;
        }
        if (selected.Count != expected.Count || positions.Where((value, index) => value != positions[0] + index).Any()) return null;
        ulong totalFee = 0; ulong priorOffset = 0;
        for (var index = 0; index < expected.Count; index++)
        {
            var actual = selected[index]; var original = expected[index];
            if (!Text(actual, "id", ids[index]) || !Text(actual, "sender", original.Sender.ToString())
                || !Number(actual, "confirmed-round", out var confirmed) || confirmed != round
                || !Number(actual, "intra-round-offset", out var offset) || index > 0 && offset <= priorOffset
                || !Number(actual, "fee", out var fee) || fee != original.Fee
                || !Number(actual, "first-valid", out var from) || from != original.FirstValid
                || !Number(actual, "last-valid", out var until) || until != original.LastValid
                || !Text(actual, "note", Convert.ToBase64String(original.Note)) || !Text(actual, "group", group)
                || !OptionalText(actual, "genesis-hash", Convert.ToBase64String(original.GenesisHash.Bytes))
                || !OptionalText(actual, "genesis-id", original.GenesisId)
                || !Absent(actual, "auth-addr", "lease", "created-asset-index", "created-application-index")
                || !(original.RekeyTo is null ? Absent(actual, "rekey-to") : Text(actual, "rekey-to", original.RekeyTo.ToString()))
                || !Body(actual, original)) return null;
            totalFee = checked(totalFee + fee); priorOffset = offset;
        }
        return new(round, timestamp, totalFee, selected, CanonicalJson.Hash(block));
    }

    private static bool Body(JsonElement actual, Transaction expected)
    {
        if (expected is PaymentTransaction payment)
            return Text(actual, "tx-type", "pay") && OnlyBody(actual, "payment-transaction")
                && Object(actual, "payment-transaction", out var body) && Text(body, "receiver", payment.Receiver.ToString())
                && Number(body, "amount", out var amount) && amount == payment.Amount
                && Absent(body, "close-remainder-to") && ZeroOrAbsent(body, "close-amount")
                && ZeroOrAbsent(actual, "closing-amount") && EmptyArray(actual, "inner-txns");
        if (expected is AssetTransferTransaction transfer)
            return Text(actual, "tx-type", "axfer") && OnlyBody(actual, "asset-transfer-transaction")
                && Object(actual, "asset-transfer-transaction", out var body) && Text(body, "receiver", transfer.AssetReceiver.ToString())
                && Number(body, "amount", out var amount) && amount == transfer.AssetAmount
                && Number(body, "asset-id", out var asset) && asset == transfer.XferAsset
                && Absent(body, "close-to", "sender") && ZeroOrAbsent(body, "close-amount") && EmptyArray(actual, "inner-txns");
        if (expected is not ApplicationCallTransaction app) return false;
        if (!Text(actual, "tx-type", "appl") || !OnlyBody(actual, "application-transaction")
            || !Object(actual, "application-transaction", out var application)
            || !Number(application, "application-id", out var appId) || appId != app.ApplicationId
            || !Text(application, "on-completion", expected is ApplicationOptInTransaction ? "optin" : "noop")
            || !Strings(application, "application-args", (app.ApplicationArgs ?? []).Select(Convert.ToBase64String))
            || !Strings(application, "accounts", (app.Accounts ?? []).Select(address => address.ToString()))
            || !Numbers(application, "foreign-apps", app.ForeignApps ?? [])
            || !Numbers(application, "foreign-assets", app.ForeignAssets ?? [])
            || !EmptyArray(application, "box-references") || !EmptyArray(application, "access")
            || !Absent(application, "approval-program", "clear-state-program")
            || !EmptySchema(application, "local-state-schema") || !EmptySchema(application, "global-state-schema")
            || !ZeroOrAbsent(application, "extra-program-pages") || !ZeroOrAbsent(application, "reject-version")) return false;
        return true;
    }

    private static bool Strings(JsonElement value, string name, IEnumerable<string> expected)
    {
        var items = expected.ToArray();
        if (!value.TryGetProperty(name, out var array)) return items.Length == 0;
        return array.ValueKind == JsonValueKind.Array && array.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
            && array.EnumerateArray().Select(item => item.GetString()).SequenceEqual(items);
    }
    private static bool Numbers(JsonElement value, string name, IEnumerable<ulong> expected)
    {
        var items = expected.ToArray();
        if (!value.TryGetProperty(name, out var array)) return items.Length == 0;
        return array.ValueKind == JsonValueKind.Array && array.GetArrayLength() == items.Length
            && array.EnumerateArray().Select((item, index) => item.ValueKind == JsonValueKind.Number
                && item.TryGetUInt64(out var number) && number == items[index]).All(match => match);
    }
    private static bool OnlyBody(JsonElement value, string body) => value.EnumerateObject()
        .All(property => !property.Name.EndsWith("-transaction", StringComparison.Ordinal) || property.Name == body);
    private static bool Absent(JsonElement value, params string[] fields) => fields.All(field => !value.TryGetProperty(field, out _));
    private static bool OptionalText(JsonElement value, string field, string expected) => !value.TryGetProperty(field, out _) || Text(value, field, expected);
    private static bool ZeroOrAbsent(JsonElement value, string field) => !value.TryGetProperty(field, out _) || Number(value, field, out var number) && number == 0;
    private static bool EmptyArray(JsonElement value, string field) => !value.TryGetProperty(field, out _) || Array(value, field, out var array) && array.GetArrayLength() == 0;
    private static bool EmptySchema(JsonElement value, string field) => !value.TryGetProperty(field, out _)
        || Object(value, field, out var schema) && Number(schema, "num-byte-slice", out var bytes) && bytes == 0
            && Number(schema, "num-uint", out var integers) && integers == 0;
    private static bool Number(JsonElement value, string field, out ulong number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var item)
            && item.ValueKind == JsonValueKind.Number && item.TryGetUInt64(out number);
    }
    private static bool Text(JsonElement value, string field, string expected) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String && item.GetString() == expected;
    private static bool Object(JsonElement value, string field, out JsonElement item)
    {
        item = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out item) && item.ValueKind == JsonValueKind.Object;
    }
    private static bool Array(JsonElement value, string field, out JsonElement item)
    {
        item = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out item) && item.ValueKind == JsonValueKind.Array;
    }
}
