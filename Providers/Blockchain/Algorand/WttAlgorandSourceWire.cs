using System.Buffers.Binary;
using Algorand;
using Algorand.Algod.Model.Transactions;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

internal sealed record WttAlgorandSourceParameters(string Stage, string PreparationKey, string Owner,
    ulong CoreApplication, ulong TokenApplication, ulong Asset, ulong Amount, ulong RelayerFee,
    ushort DestinationChain, string RecipientHex, bool Wrapped, ushort OriginChain, string OriginAddressHex,
    string AssetCreator, ulong MessageFee, ulong FirstValid, ulong LastValid, string GenesisHash,
    string GenesisId, ulong MinimumFee, ulong RegularFee, ulong ApplicationFee);

/// <summary>Atomic processor-derived WTT publication grammar after independently proven storage prerequisites.</summary>
internal static class WttAlgorandSourceWire
{
    internal static IReadOnlyList<Transaction> Build(WttAlgorandSourceParameters input)
    {
        if (input.Stage is not ("Transfer" or "Attest") || string.IsNullOrWhiteSpace(input.PreparationKey)
            || input.CoreApplication == 0 || input.TokenApplication == 0 || input.CoreApplication == input.TokenApplication
            || input.FirstValid == 0 || input.LastValid < input.FirstValid || input.LastValid - input.FirstValid > 1000
            || input.MinimumFee == 0 || input.RegularFee < input.MinimumFee
            || (UInt128)input.ApplicationFee < (UInt128)input.MinimumFee * (input.MessageFee == 0 ? 2u : 3u)
            || input.Stage == "Transfer" && (input.Amount == 0 || input.RelayerFee > input.Amount
                || input.DestinationChain is 0 or 8 || !Hex32(input.RecipientHex))
            || input.Wrapped && (input.Asset == 0 || input.OriginChain is 0 or 8 || !Hex32(input.OriginAddressHex))
            || input.Stage == "Attest" && input.Wrapped)
            throw new ArgumentException("Invalid Algorand WTT source publication parameters.");
        var owner = new Address(input.Owner);
        var tokenAddress = Address.ForApplication(input.TokenApplication);
        var sequence = new Address(WttAlgorandStorage.Address(input.CoreApplication, 0, tokenAddress.Bytes));
        var custody = new Address(input.Wrapped
            ? WttAlgorandStorage.Address(input.TokenApplication, input.OriginChain, Convert.FromHexString(input.OriginAddressHex))
            : WttAlgorandStorage.Address(input.TokenApplication, input.Asset, "native"u8));
        var creator = input.Asset == 0 ? custody : new Address(input.AssetCreator);
        if (input.Wrapped && creator.ToString() != custody.ToString())
            throw new ArgumentException("Wrapped ASA creator must be its canonical origin storage account.");
        var transactions = new List<Transaction> {
            new ApplicationNoopTransaction { Sender = owner, ApplicationId = input.TokenApplication,
                ApplicationArgs = ["nop"u8.ToArray()] }
        };
        if (input.MessageFee > 0)
            transactions.Add(new PaymentTransaction { Sender = owner, Receiver = tokenAddress, Amount = input.MessageFee });
        if (input.Stage == "Transfer")
            transactions.Add(input.Asset == 0
                ? new PaymentTransaction { Sender = owner, Receiver = custody, Amount = input.Amount }
                : new AssetTransferTransaction { Sender = owner, AssetReceiver = custody, XferAsset = input.Asset, AssetAmount = input.Amount });
        transactions.Add(new ApplicationNoopTransaction {
            Sender = owner, ApplicationId = input.TokenApplication,
            ApplicationArgs = input.Stage == "Transfer"
                ? ["sendTransfer"u8.ToArray(), UInt64(input.Asset), UInt64(input.Amount), Convert.FromHexString(input.RecipientHex),
                    UInt64(input.DestinationChain), UInt64(input.RelayerFee)]
                : ["attestToken"u8.ToArray(), UInt64(input.Asset)],
            Accounts = input.Stage == "Transfer"
                ? [sequence, custody, creator, Address.ForApplication(input.CoreApplication)]
                : input.Asset == 0 ? [sequence, custody, Address.ForApplication(input.CoreApplication)]
                    : [sequence, custody, Address.ForApplication(input.CoreApplication), creator],
            ForeignApps = [input.CoreApplication],
            ForeignAssets = input.Asset == 0 ? [] : [input.Asset]
        });
        WttAlgorandGroup.Seal(transactions, "azoa:wtt:source:v1:", input.PreparationKey, input.FirstValid,
            input.LastValid, input.GenesisHash, input.GenesisId,
            index => index == transactions.Count - 1 ? input.ApplicationFee : input.RegularFee);
        return transactions;
    }

    internal static IReadOnlyList<SignedTransaction> ValidateSigned(WttAlgorandSourceParameters input, IReadOnlyList<string> envelopes)
        => WttAlgorandGroup.ValidateSingleOwner(Build(input), envelopes, input.Owner);

    private static byte[] UInt64(ulong value)
    {
        var bytes = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, value); return bytes;
    }
    private static bool Hex32(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
}
