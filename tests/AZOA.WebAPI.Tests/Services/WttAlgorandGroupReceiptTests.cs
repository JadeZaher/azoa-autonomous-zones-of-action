using System.Text.Json;
using Algorand;
using Algorand.Algod.Model.Transactions;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class AlgorandGroupReceiptTests
{
    [Theory]
    [InlineData("exact", true)]
    [InlineData("amount", false)]
    [InlineData("sender", false)]
    [InlineData("fee", false)]
    [InlineData("note", false)]
    [InlineData("foreign-app", false)]
    [InlineData("argument", false)]
    [InlineData("rekey", false)]
    [InlineData("order", false)]
    [InlineData("extra-group-member", false)]
    [InlineData("genesis", false)]
    public void Finalized_group_requires_exact_wire_fields_and_all_members(string mutation, bool accepted)
    {
        var owner = new Address(new byte[32]).ToString();
        var input = new WttAlgorandSourceParameters("Transfer", "key", owner, 7, 9, 0, 10000, 0,
            2, new string('a', 64), false, 8, new string('0', 64), owner, 0, 10, 1010,
            Convert.ToBase64String(new byte[32]), "devnet-v1", 1000, 1000, 2000);
        var expected = WttAlgorandSourceWire.Build(input);
        var transactions = expected.Select((transaction, index) => Indexed(transaction, 20, (ulong)index)).ToList();
        if (mutation == "amount") Body(transactions[1], "payment-transaction")["amount"] = 9999UL;
        if (mutation == "sender") transactions[0]["sender"] = Address.ForApplication(9).ToString();
        if (mutation == "fee") transactions[0]["fee"] = 1001UL;
        if (mutation == "note") transactions[0]["note"] = "AA==";
        if (mutation == "foreign-app") Body(transactions[^1], "application-transaction")["foreign-apps"] = new ulong[] { 8 };
        if (mutation == "argument") Body(transactions[^1], "application-transaction")["application-args"] = new[] { "AA==" };
        if (mutation == "rekey") transactions[0]["rekey-to"] = owner;
        if (mutation == "order") transactions.Reverse();
        if (mutation == "extra-group-member") { var extra = Indexed(expected[0], 20, 8); extra["id"] = "foreign"; transactions.Add(extra); }
        var block = Block(expected, transactions, mutation == "genesis");
        var result = AlgorandGroupReceipt.Read(expected, block);
        Assert.Equal(accepted, result is not null);
        if (accepted) Assert.Equal(4000UL, result!.NetworkFee);
    }

    [Fact]
    public void Storage_optin_accepts_only_its_exact_protocol_rekey_and_zero_fee_member()
    {
        var owner = new Address(new byte[32]).ToString();
        var expected = WttAlgorandStorageWire.Build(new("storage", owner, 7, 0, new string('a', 64),
            1002000, 2000, 1000, 10, 1010, Convert.ToBase64String(new byte[32]), "devnet-v1"));
        var transactions = expected.Select((transaction, index) => Indexed(transaction, 20, (ulong)index)).ToList();
        Assert.NotNull(AlgorandGroupReceipt.Read(expected, Block(expected, transactions)));
        transactions[1]["rekey-to"] = Address.ForApplication(9).ToString();
        Assert.Null(AlgorandGroupReceipt.Read(expected, Block(expected, transactions)));
    }

    internal static Dictionary<string, object> Indexed(Transaction tx, ulong round, ulong offset)
    {
        var value = new Dictionary<string, object> { ["id"] = tx.TxID(), ["sender"] = tx.Sender.ToString(),
            ["confirmed-round"] = round, ["intra-round-offset"] = offset, ["fee"] = tx.Fee!.Value,
            ["first-valid"] = tx.FirstValid!.Value, ["last-valid"] = tx.LastValid!.Value,
            ["note"] = Convert.ToBase64String(tx.Note), ["group"] = Convert.ToBase64String(tx.Group.Bytes) };
        if (tx.RekeyTo is not null) value["rekey-to"] = tx.RekeyTo.ToString();
        if (tx is PaymentTransaction payment)
        {
            value["tx-type"] = "pay";
            value["payment-transaction"] = new Dictionary<string, object> { ["amount"] = payment.Amount!.Value, ["receiver"] = payment.Receiver.ToString() };
        }
        else if (tx is AssetTransferTransaction transfer)
        {
            value["tx-type"] = "axfer";
            value["asset-transfer-transaction"] = new Dictionary<string, object> { ["amount"] = transfer.AssetAmount!.Value,
                ["receiver"] = transfer.AssetReceiver.ToString(), ["asset-id"] = transfer.XferAsset!.Value };
        }
        else if (tx is ApplicationCallTransaction app)
        {
            value["tx-type"] = "appl";
            value["application-transaction"] = new Dictionary<string, object> { ["application-id"] = app.ApplicationId!.Value,
                ["on-completion"] = tx is ApplicationOptInTransaction ? "optin" : "noop",
                ["application-args"] = (app.ApplicationArgs ?? []).Select(Convert.ToBase64String).ToArray(),
                ["accounts"] = (app.Accounts ?? []).Select(address => address.ToString()).ToArray(),
                ["foreign-apps"] = (app.ForeignApps ?? []).ToArray(), ["foreign-assets"] = (app.ForeignAssets ?? []).ToArray() };
        }
        return value;
    }
    internal static JsonElement Block(IReadOnlyList<Transaction> expected, IReadOnlyList<Dictionary<string, object>> transactions, bool badGenesis = false)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["round"] = 20UL, ["timestamp"] = 1800000000UL,
            ["genesis-hash"] = badGenesis ? "bad" : Convert.ToBase64String(expected[0].GenesisHash.Bytes),
            ["genesis-id"] = expected[0].GenesisId, ["transactions"] = transactions });
    private static Dictionary<string, object> Body(Dictionary<string, object> tx, string name) => (Dictionary<string, object>)tx[name];
}
