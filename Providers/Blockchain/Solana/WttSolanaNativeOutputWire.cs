using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Unwraps only the accepted net native amount through a distinct transient account, preserving the recipient ATA.</summary>
internal static class WttSolanaNativeOutputWire
{
    internal const string Domain = "azoa:wtt:recipient-unwrap:1:";

    internal static (SolanaSeededNativeAccount Account, IReadOnlyList<TransactionInstruction> Instructions) Build(
        WttOperationIntent intent, WttNativeOutputQuote quote, string preparationKey, PublicKey guard, byte[] rent,
        uint computeUnits, ulong computePrice)
    {
        var mint = SolanaSeededNativeAccount.Mint;
        var owner = new PublicKey(intent.RecipientAddress);
        var source = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, mint);
        var net = WttPayloadCodec.ParseAmount(intent.Amount.Amount) - WttPayloadCodec.ParseAmount(intent.Amount.Fee);
        var exact = WttPayloadCodec.ParseAmount(WttPayloadCodec.Denormalize(net.ToString(System.Globalization.CultureInfo.InvariantCulture), 9));
        if (!intent.UnwrapNative || intent.Target.ProtocolChain != 1 || intent.Target.ChainType != "Solana"
            || intent.Asset.OriginChain != 1 || intent.Asset.OriginAddress != Convert.ToHexStringLower(mint.KeyBytes)
            || intent.TargetDecimals != 9 || exact <= 0 || exact > ulong.MaxValue || UInt64Text.Parse(quote.NativeAmount) != (ulong)exact
            || quote.Version != 1 || quote.IntentHash != CanonicalJson.Hash(intent) || quote.ProtocolIdentity != intent.Target.ProtocolIdentity
            || quote.RecipientAddress != owner.Key || quote.MintAddress != mint.Key || quote.SourceTokenAccount != source.Key
            || UInt64Text.Parse(quote.TemporaryRent) != WttSolanaCostGuard.MinimumBalance(rent, 165)
            || computeUnits is 0 or > 1_400_000)
            throw new InvalidDataException("Native output differs from the original requested net amount, mint, recipient or exact temporary rent.");
        var temporary = SolanaSeededNativeAccount.Create(owner.Key, preparationKey, UInt64Text.Parse(quote.TemporaryRent), 0, Domain);
        if (temporary.Address == source.Key) throw new InvalidDataException("Native output cannot close the recipient's persistent ATA.");
        var instructions = new List<TransactionInstruction>
        {
            SolanaComputeBudget.Limit(computeUnits), SolanaComputeBudget.Price(computePrice),
            SolanaRentGuard.Build(guard, rent),
            MemoProgram.NewMemoV2(CanonicalJson.Hash(new { Domain, Identity = preparationKey }), owner),
        };
        instructions.AddRange(temporary.Before(owner.Key, preparationKey));
        instructions.Add(TokenProgram.TransferChecked(source, new PublicKey(temporary.Address), (ulong)exact, 9, owner, mint));
        instructions.Add(temporary.After(owner.Key, preparationKey));
        return (temporary, instructions);
    }
}
