using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

internal sealed record WttAlgorandStorageParameters(string PreparationKey, string Payer, ulong Application,
    ulong Index, string EmitterHex, ulong Deposit, ulong Fee, ulong MinimumFee,
    ulong FirstValid, ulong LastValid, string GenesisHash, string GenesisId);

/// <summary>Exact funding and undelegated protocol-template opt-in for one Wormhole storage account.</summary>
internal static class WttAlgorandStorageWire
{
    internal const ulong ProtocolSeedDeposit = 1_002_000;

    internal static IReadOnlyList<Transaction> Build(WttAlgorandStorageParameters input)
    {
        if (string.IsNullOrWhiteSpace(input.PreparationKey) || input.PreparationKey.Length > 512
            || input.Application == 0 || input.EmitterHex is not { Length: > 0 and <= 128 }
            || input.EmitterHex.Length % 2 != 0 || !input.EmitterHex.All(char.IsAsciiHexDigitLower)
            || input.Deposit != ProtocolSeedDeposit || input.MinimumFee == 0
            || (UInt128)input.Fee < (UInt128)input.MinimumFee * 2
            || input.FirstValid == 0 || input.LastValid < input.FirstValid || input.LastValid - input.FirstValid > 1000)
            throw new InvalidDataException("WTT storage creation requires an exact protocol deposit and bounded group.");
        var storage = new Address(WttAlgorandStorage.Address(input.Application, input.Index, Convert.FromHexString(input.EmitterHex)));
        var transactions = new Transaction[]
        {
            new PaymentTransaction { Sender = new Address(input.Payer), Receiver = storage, Amount = input.Deposit },
            new ApplicationOptInTransaction { Sender = storage, ApplicationId = input.Application,
                RekeyTo = Address.ForApplication(input.Application) }
        };
        WttAlgorandGroup.Seal(transactions, "azoa:wtt:storage:v1:", input.PreparationKey, input.FirstValid,
            input.LastValid, input.GenesisHash, input.GenesisId, index => index == 0 ? input.Fee : 0);
        return transactions;
    }

    internal static SignedTransaction ProtocolEnvelope(WttAlgorandStorageParameters input)
    {
        var transaction = Build(input)[1];
        var program = WttAlgorandStorage.Program(input.Application, input.Index, Convert.FromHexString(input.EmitterHex));
        return new(transaction, null, null, new LogicsigSignature(program), null);
    }

    internal static IReadOnlyList<SignedTransaction> ValidateSigned(WttAlgorandStorageParameters input, IReadOnlyList<string> envelopes)
    {
        if (envelopes.Count != 2) throw new InvalidDataException("WTT storage creation requires exactly two group members.");
        var expected = Build(input);
        var payment = AlgorandSignedEnvelope.DecodeCanonical(envelopes[0]);
        var optIn = AlgorandSignedEnvelope.DecodeCanonical(envelopes[1]);
        if (!Encoder.EncodeToMsgPackOrdered(payment.Tx).AsSpan().SequenceEqual(Encoder.EncodeToMsgPackOrdered(expected[0])))
            throw new InvalidDataException("WTT storage payment differs from the frozen plan.");
        AlgorandSignedEnvelope.RequireSingleSender(payment, input.Payer);
        if (!Encoder.EncodeToMsgPackOrdered(optIn).AsSpan().SequenceEqual(Encoder.EncodeToMsgPackOrdered(ProtocolEnvelope(input))))
            throw new InvalidDataException("WTT storage opt-in is not the exact undelegated protocol template and group.");
        return [payment, optIn];
    }
}
