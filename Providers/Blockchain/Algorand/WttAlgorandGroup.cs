using System.Security.Cryptography;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Binds WTT group members to one physical genesis, validity window, fee schedule and preparation identity.</summary>
internal static class WttAlgorandGroup
{
    internal static void Seal(IReadOnlyList<Transaction> transactions, string domain, string preparationKey,
        ulong firstValid, ulong lastValid, string genesisHash, string genesisId, Func<int, ulong> fee)
    {
        if (transactions.Count is < 1 or > 16 || string.IsNullOrWhiteSpace(preparationKey) || preparationKey.Length > 512
            || firstValid == 0 || lastValid < firstValid || lastValid - firstValid > 1000)
            throw new InvalidDataException("WTT group requires a bounded validity window and exact preparation identity.");
        byte[] genesis;
        try { genesis = Convert.FromBase64String(genesisHash); }
        catch (FormatException) { throw new InvalidDataException("WTT group genesis is malformed."); }
        if (genesis.Length != 32 || Convert.ToBase64String(genesis) != genesisHash)
            throw new InvalidDataException("WTT group genesis is not canonical.");
        var note = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(domain + preparationKey));
        for (var index = 0; index < transactions.Count; index++)
        {
            var transaction = transactions[index];
            transaction.FirstValid = firstValid; transaction.LastValid = lastValid;
            transaction.GenesisHash = new Digest(genesisHash); transaction.GenesisId = genesisId;
            transaction.Note = note; transaction.SetFee(fee(index));
        }
        TxGroup.AssignGroupID(transactions.ToArray());
    }

    internal static IReadOnlyList<SignedTransaction> ValidateSingleOwner(IReadOnlyList<Transaction> expected,
        IReadOnlyList<string> envelopes, string owner)
    {
        if (envelopes.Count != expected.Count) throw new InvalidDataException("WTT group member count differs from the frozen plan.");
        var result = new List<SignedTransaction>();
        for (var index = 0; index < expected.Count; index++)
        {
            var signed = AlgorandSignedEnvelope.DecodeCanonical(envelopes[index]);
            if (!Encoder.EncodeToMsgPackOrdered(signed.Tx).AsSpan().SequenceEqual(Encoder.EncodeToMsgPackOrdered(expected[index])))
                throw new InvalidDataException("WTT group member differs from the exact saved plan.");
            AlgorandSignedEnvelope.RequireSingleSender(signed, owner);
            result.Add(signed);
        }
        return result;
    }
}
