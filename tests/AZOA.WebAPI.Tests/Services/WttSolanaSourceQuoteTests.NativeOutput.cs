using System.Buffers.Binary;
using System.Text.Json;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Programs;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed partial class WttSolanaSourceQuoteTests
{
    [Fact]
    public async Task NativeOutputUsesRealRecipientSignatureSavedMessageAndFinalizedNetEffect()
    {
        using var rpc = new SourceRpc(native: true); var source = await rpc.IntentAsync();
        var (provider, intent, proofs) = rpc.Target(source, "valid");
        var targetQuote = (await provider.QuoteWttTargetAsync(intent, proofs)).Result!;
        using var signing = new SigningFixture(rpc, intent.TargetAuthority.Context);
        var redemption = (await signing.Provider.PrepareWttTargetAsync(intent, proofs, targetQuote, "Redeem", 0)).Result!;
        rpc.FinalizeRedemption(intent, proofs, redemption, "valid");
        var quote = await signing.Provider.QuoteWttNativeOutputAsync(intent, proofs, targetQuote, redemption);
        Assert.False(quote.IsError, quote.Message); Assert.Equal("1000", quote.Result!.NativeAmount);
        var consent = new WttNativeOutputConsent(1, "recipient-output", quote.Result.QuoteId, quote.Result.NetworkFee, quote.Result.TemporaryRent);
        var before = signing.CustodyCalls; signing.AllowCustody = false;
        var unsigned = await signing.Provider.PrepareWttNativeOutputAsync(intent, quote.Result, consent, 0);
        Assert.False(unsigned.IsError, unsigned.Message); Assert.Equal(before, signing.CustodyCalls);
        var repeated = await signing.Provider.PrepareWttNativeOutputAsync(intent, quote.Result, consent, 0);
        Assert.Equal(unsigned.Result, repeated.Result);
        using var key = new SigningKeyMaterial(rpc.OwnerAccount.PrivateKey.KeyBytes.ToArray());
        var signed = new SolanaTransactionSigner().Sign(Convert.FromBase64String(unsigned.Result!.UnsignedTransactionBase64), key);
        Assert.False(signed.IsError, signed.Message);
        var changed = signed.Result!.ToArray(); changed[^1] ^= 1;
        Assert.True((await signing.Provider.AcceptWttNativeOutputSignatureAsync(intent, quote.Result, consent, unsigned.Result,
            Convert.ToBase64String(changed))).IsError);
        var accepted = await signing.Provider.AcceptWttNativeOutputSignatureAsync(intent, quote.Result, consent, unsigned.Result,
            Convert.ToBase64String(signed.Result!));
        Assert.False(accepted.IsError, accepted.Message); Assert.Equal(before, signing.CustodyCalls);
        rpc.LoseSendResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => signing.Provider.SubmitWttNativeOutputAsync(intent, quote.Result, consent, accepted.Result!));
        await signing.Provider.SubmitWttNativeOutputAsync(intent, quote.Result, consent, accepted.Result!);
        Assert.Equal(rpc.Sent[^2], rpc.Sent[^1]);
        rpc.FinalizeNativeOutput(accepted.Result!, quote.Result, alteredCredit: false);
        var observed = await signing.Provider.ObserveWttNativeOutputAsync(intent, quote.Result, consent, accepted.Result!);
        Assert.Equal("Confirmed", observed.Result!.Stage.Verdict); Assert.Equal("1000", observed.Result.NativeAmountReceived);
        Assert.Equal("2039280", observed.Result.TemporaryRentReturned);
        rpc.FinalizeNativeOutput(accepted.Result!, quote.Result, alteredCredit: true);
        Assert.Equal("NeedsOperator", (await signing.Provider.ObserveWttNativeOutputAsync(intent, quote.Result, consent, accepted.Result!)).Result!.Stage.Verdict);
    }

    [Fact]
    public async Task NativeOutputClosesOnlyItsDerivedTransientAccountAndTransfersOnlyNetRedemption()
    {
        using var rpc = new SourceRpc(); var source = await rpc.IntentAsync();
        var mint = SolanaSeededNativeAccount.Mint; var owner = rpc.Owner;
        var ata = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, mint);
        var intent = source with { Source = source.Target, Target = source.Source, RecipientAddress = owner.Key,
            RecipientProtocolAddress = Convert.ToHexStringLower(ata.KeyBytes), TargetDecimals = 9, UnwrapNative = true,
            Asset = source.Asset with { OriginChain = 1, OriginAddress = Convert.ToHexStringLower(mint.KeyBytes) },
            Amount = WttPayloadCodec.Normalize("1000", "100", 9) };
        var rent = new byte[17]; BinaryPrimitives.WriteUInt64LittleEndian(rent, 3480);
        BinaryPrimitives.WriteDoubleLittleEndian(rent.AsSpan(8), 2); rent[16] = 50;
        var quote = new WttNativeOutputQuote(1, "offer", CanonicalJson.Hash(intent), "redemption", "evidence",
            intent.Target.ProtocolIdentity, owner.Key, mint.Key, ata.Key, "900", "5000", "2039280", "{}", DateTimeOffset.UtcNow.AddMinutes(1));
        var (account, instructions) = WttSolanaNativeOutputWire.Build(intent, quote, "operation:unwrap:0", rpc.Guard, rent, 200000, 0);
        Assert.NotEqual(ata.Key, account.Address); Assert.Equal(0UL, account.Funding);
        Assert.Equal(9, instructions.Count); Assert.Equal(new byte[] { 6 }.Concat(rent), instructions[2].Data);
        var transfer = instructions[^2]; Assert.Equal((byte)12, transfer.Data[0]);
        Assert.Equal(900UL, BinaryPrimitives.ReadUInt64LittleEndian(transfer.Data.AsSpan(1))); Assert.Equal((byte)9, transfer.Data[9]);
        Assert.Equal(new[] { ata.Key, mint.Key, account.Address, owner.Key }, transfer.Keys.Select(value => value.PublicKey));
        var close = instructions[^1]; Assert.Equal(new byte[] { 9 }, close.Data);
        Assert.Equal(new[] { account.Address, owner.Key, owner.Key }, close.Keys.Select(value => value.PublicKey));
        Assert.NotEqual(account.Address, WttSolanaNativeOutputWire.Build(intent, quote, "operation:unwrap:1", rpc.Guard, rent, 200000, 0).Account.Address);
        Assert.NotEqual(account.Address, JupiterNativeAccount.Create(owner.Key, "operation:unwrap:0", 2039280, 0).Address);
        Assert.Throws<InvalidDataException>(() => WttSolanaNativeOutputWire.Build(intent, quote with { NativeAmount = "1000" },
            "operation:unwrap:0", rpc.Guard, rent, 200000, 0));
        Assert.Throws<InvalidDataException>(() => WttSolanaNativeOutputWire.Build(intent with { UnwrapNative = false }, quote,
            "operation:unwrap:0", rpc.Guard, rent, 200000, 0));
    }

    private sealed partial class SourceRpc
    {
        private object? NativeOutputSnapshot(JsonElement args)
        {
            if (_mint.Key != SolanaSeededNativeAccount.Mint.Key || args[0].GetArrayLength() != 4 || args[0][0].GetString() != _mint.Key) return null;
            return new { context = new { slot = 101UL }, value = args[0].EnumerateArray().Select(value => _accounts.GetValueOrDefault(value.GetString()!)).ToArray() };
        }

        public void FinalizeNativeOutput(WttStagePreparation prepared, WttNativeOutputQuote quote, bool alteredCredit)
        {
            using var recovery = JsonDocument.Parse(prepared.RecoveryMetadata);
            var message = SolanaMessageEnvelope.Read(Convert.FromBase64String(recovery.RootElement.GetProperty("MessageBase64").GetString()!));
            var keys = message.StaticKeys.Select(value => value.Key).ToArray();
            var source = Array.IndexOf(keys, quote.SourceTokenAccount); var recipient = Array.IndexOf(keys, quote.RecipientAddress);
            var transfer = message.Instructions.Single(value => keys[value.ProgramIndex] == TokenProgram.ProgramIdKey.Key && value.Data[0] == 12);
            var temporary = transfer.AccountIndices[2];
            var pre = new ulong[keys.Length]; var post = new ulong[keys.Length];
            pre[source] = 3039280; post[source] = 3038280;
            pre[recipient] = 1000000; post[recipient] = alteredCredit ? 995999UL : 996000UL;
            pre[temporary] = 0; post[temporary] = 0;
            object Balance(string amount) => new { accountIndex = source, mint = quote.MintAddress, owner = quote.RecipientAddress,
                programId = TokenProgram.ProgramIdKey.Key, uiTokenAmount = new { amount, decimals = 9 } };
            publicationSignature = prepared.TransactionId; observationMutation = "valid";
            FinalizedTransaction = new { slot = 102UL, transaction = new[] { prepared.SignedTransactionBase64, "base64" },
                meta = new { err = (object?)null, fee = 5000UL, preBalances = pre, postBalances = post,
                    preTokenBalances = new[] { Balance("1000000") }, postTokenBalances = new[] { Balance("999000") },
                    loadedAddresses = new { writable = Array.Empty<string>(), @readonly = Array.Empty<string>() } } };
        }
    }
}
