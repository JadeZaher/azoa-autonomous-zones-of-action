using System.Buffers.Binary;
using Algorand;
using Algorand.Algod.Model.Transactions;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

internal sealed record WttAlgorandHoldingParameters(string Stage, string PreparationKey, string Owner,
    ulong TokenApplication, ulong Asset, ulong MinimumFee, ulong RegularFee, ulong ApplicationFee,
    ulong FirstValid, ulong LastValid, string GenesisHash, string GenesisId);

/// <summary>Closed bridge-custody or participant-owned ASA holding prerequisites.</summary>
internal static class WttAlgorandHoldingWire
{
    internal const ulong HoldingDeposit = 100_000;

    internal static IReadOnlyList<Transaction> Build(WttAlgorandHoldingParameters input)
    {
        if (input.Stage is not ("CustodyOptIn" or "RecipientOptIn") || input.Asset == 0 || input.TokenApplication == 0
            || string.IsNullOrWhiteSpace(input.PreparationKey) || input.PreparationKey.Length > 512
            || input.MinimumFee == 0 || input.RegularFee < input.MinimumFee
            || input.Stage == "CustodyOptIn" && (UInt128)input.ApplicationFee < (UInt128)input.MinimumFee * 2
            || input.Stage == "RecipientOptIn" && input.ApplicationFee != 0
            || input.FirstValid == 0 || input.LastValid < input.FirstValid || input.LastValid - input.FirstValid > 1000)
            throw new InvalidDataException("WTT ASA holding prerequisite is not a bounded exact plan.");
        var owner = new Address(input.Owner);
        var custody = new Address(WttAlgorandStorage.Address(input.TokenApplication, input.Asset, "native"u8));
        var encodedAsset = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(encodedAsset, input.Asset);
        Transaction[] transactions = input.Stage == "RecipientOptIn"
            ? [new AssetTransferTransaction { Sender = owner, AssetReceiver = owner, XferAsset = input.Asset, AssetAmount = 0 }]
            : [new PaymentTransaction { Sender = owner, Receiver = custody, Amount = HoldingDeposit },
                new ApplicationNoopTransaction { Sender = owner, ApplicationId = input.TokenApplication,
                    ApplicationArgs = ["optin"u8.ToArray(), encodedAsset], Accounts = [custody], ForeignAssets = [input.Asset] }];
        WttAlgorandGroup.Seal(transactions, "azoa:wtt:holding:v1:", input.PreparationKey, input.FirstValid,
            input.LastValid, input.GenesisHash, input.GenesisId, index => index == 1 ? input.ApplicationFee : input.RegularFee);
        return transactions;
    }

    internal static IReadOnlyList<SignedTransaction> ValidateSigned(WttAlgorandHoldingParameters input, IReadOnlyList<string> envelopes)
        => WttAlgorandGroup.ValidateSingleOwner(Build(input), envelopes, input.Owner);
}
