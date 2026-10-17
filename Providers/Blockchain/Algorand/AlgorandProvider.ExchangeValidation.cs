using System.Text;
using Algorand;
using Algorand.Algod.Model.Transactions;
using AlgorandEncoder = Algorand.Utils.Encoder;
using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    private Transaction[] ValidateExchangePrepared(PreparedExchangeWire prepared, ExchangeUnsignedPlan? saved = null)
    {
        if (!ValidExchangeIntent(prepared.Intent) || prepared.PreparationId != ExchangePreparationId(prepared.Intent)
            || prepared.SignedTransactionsBase64.Count != prepared.Intent.Legs.Count + 1
            || prepared.TransactionIds.Count != prepared.SignedTransactionsBase64.Count)
            throw new InvalidOperationException("Prepared exchange identity is incomplete.");
        var envelopes = prepared.SignedTransactionsBase64.Select(AlgorandSignedEnvelope.DecodeCanonical).ToArray();
        var transactions = envelopes.Select(envelope => envelope.Tx).ToArray();
        for (var index = 0; index < envelopes.Length; index++)
        {
            var sender = index == prepared.Intent.Legs.Count ? prepared.Intent.FeePayerAddress : prepared.Intent.Legs[index].Sender;
            AlgorandSignedEnvelope.RequireSingleSender(envelopes[index], sender);
            if (transactions[index].TxID() != prepared.TransactionIds[index]
                || transactions[index].Group is null || Convert.ToBase64String(transactions[index].Group.Bytes) != prepared.GroupId)
                throw new InvalidOperationException("Prepared exchange member identity changed.");
        }
        ValidateExchangeUnsigned(prepared.Intent, transactions, saved);
        return transactions;
    }

    private void ValidateExchangeUnsigned(ExchangeWireIntent intent, Transaction[] transactions, ExchangeUnsignedPlan? saved)
    {
        if (!ValidExchangeIntent(intent) || transactions.Length != intent.Legs.Count + 1
            || saved is not null && saved.TransactionsBase64.Count != transactions.Length)
            throw new InvalidOperationException("Exchange group shape is invalid.");
        var first = transactions[0];
        if (first.GenesisHash?.Bytes is not { Length: 32 } genesis || first.Group?.Bytes is not { Length: 32 }
            || first.FirstValid is null || first.LastValid is null || first.LastValid < first.FirstValid
            || first.LastValid.Value - first.FirstValid.Value > 1000
            || intent.ProtocolIdentity != "algorand:" + Convert.ToBase64String(genesis))
            throw new InvalidOperationException("Exchange group network or validity identity is invalid.");
        var note = Encoding.UTF8.GetBytes(CanonicalJson.Hash(intent));
        var group = Convert.ToBase64String(first.Group.Bytes);
        var ids = transactions.Select(transaction => transaction.TxID()).ToArray();
        for (var index = 0; index < transactions.Length; index++)
        {
            var actual = transactions[index];
            Transaction expected;
            if (index < intent.Legs.Count)
            {
                var leg = intent.Legs[index];
                expected = new AssetTransferTransaction { Sender = new Address(leg.Sender), AssetReceiver = new Address(leg.Recipient),
                    XferAsset = UInt64Text.Parse(leg.AssetId), AssetAmount = UInt64Text.Parse(leg.QuantityBaseUnits), Fee = 0 };
            }
            else
            {
                if (actual.Fee is null || actual.Fee == 0 || actual.Fee > UInt64Text.Parse(intent.MaximumNetworkFeeBaseUnits))
                    throw new InvalidOperationException("Exchange pooled fee exceeds its accepted bound.");
                expected = new PaymentTransaction { Sender = new Address(intent.FeePayerAddress), Receiver = new Address(intent.FeePayerAddress),
                    Amount = 0, Fee = actual.Fee };
            }
            expected.FirstValid = first.FirstValid;
            expected.LastValid = first.LastValid;
            expected.GenesisHash = first.GenesisHash;
            expected.GenesisId = first.GenesisId;
            expected.Note = note;
            expected.Group = first.Group;
            var canonical = AlgorandEncoder.EncodeToMsgPackOrdered(expected);
            if (!canonical.AsSpan().SequenceEqual(AlgorandEncoder.EncodeToMsgPackOrdered(actual))
                || saved is not null && !canonical.AsSpan().SequenceEqual(Convert.FromBase64String(saved.TransactionsBase64[index])))
                throw new InvalidOperationException("Exchange member has substituted semantics or hidden effects.");
        }
        foreach (var transaction in transactions) transaction.Group = null;
        TxGroup.AssignGroupID(transactions);
        if (Convert.ToBase64String(transactions[0].Group!.Bytes) != group
            || !transactions.Select(transaction => transaction.TxID()).SequenceEqual(ids, StringComparer.Ordinal))
            throw new InvalidOperationException("Exchange members do not form the complete original atomic group.");
    }
}
