using System.Buffers.Binary;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Solnet.Programs;
using Solnet.Wallet;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttSolanaAccountsTests
{
    private static PublicKey Key(byte value) => new(Enumerable.Repeat(value, 32).ToArray());

    [Fact]
    public void NativeTransferRequiresPayerAndMessageButNeverCustodyPdaSignature()
    {
        var token = Key(1); var core = Key(2); var payer = Key(3); var message = Key(4); var mint = Key(5);
        var instruction = WttSolanaAccounts.Transfer(token, core, payer, message, Key(6), mint,
            1, mint.KeyBytes, 42, ulong.MaxValue, 0, Key(7).KeyBytes, 8);
        Assert.Equal(token.KeyBytes, instruction.ProgramId);
        Assert.Equal(17, instruction.Keys.Count);
        Assert.Equal(new[] { payer.Key, message.Key }, instruction.Keys.Where(key => key.IsSigner).Select(key => key.PublicKey));
        Assert.Equal(WttSolanaAccounts.Derive(token, mint.KeyBytes).Key, instruction.Keys[4].PublicKey);
        Assert.False(instruction.Keys[5].IsSigner);
        Assert.False(instruction.Keys[6].IsSigner);
        Assert.Equal(core.Key, instruction.Keys[15].PublicKey);
        Assert.Equal(TokenProgram.ProgramIdKey.Key, instruction.Keys[16].PublicKey);
        Assert.Throws<ArgumentException>(() => WttSolanaAccounts.Transfer(token, core, payer, payer, Key(6), mint,
            1, mint.KeyBytes, 42, 1, 0, Key(7).KeyBytes, 8));
    }

    [Fact]
    public void WrappedTransferUsesActualOwnerAndForeignMintDerivation()
    {
        var token = Key(1); var core = Key(2); var payer = Key(3); var origin = Key(7).KeyBytes;
        var mint = WttSolanaAccounts.WrappedMint(token, 2, origin);
        var instruction = WttSolanaAccounts.Transfer(token, core, payer, Key(4), Key(6), mint,
            2, origin, 42, 1, 0, Key(8).KeyBytes, 8);
        Assert.Equal(payer.Key, instruction.Keys[3].PublicKey);
        Assert.True(instruction.Keys[3].IsSigner);
        Assert.Equal(mint.Key, instruction.Keys[4].PublicKey);
        Assert.Throws<ArgumentException>(() => WttSolanaAccounts.Transfer(token, core, payer, Key(4), Key(6), Key(5),
            2, origin, 42, 1, 0, Key(8).KeyBytes, 8));
        var bridgePayer = Key(9);
        var separated = WttSolanaAccounts.Transfer(token, core, bridgePayer, Key(4), Key(6), mint,
            2, origin, 42, 1, 0, Key(8).KeyBytes, 8, payer);
        Assert.Equal(bridgePayer.Key, separated.Keys[0].PublicKey);
        Assert.Equal(payer.Key, separated.Keys[3].PublicKey);
        Assert.True(separated.Keys[3].IsSigner);
    }

    [Fact]
    public void RedeemRetainsSeparateRecipientAndRelayerHoldingAndFullWidthClaimSequence()
    {
        var token = Key(1); var core = Key(2); var emitter = Key(7).KeyBytes; var recipient = Key(8); var relayer = Key(9);
        var instruction = WttSolanaAccounts.Complete(token, core, Key(3), Key(4), 2, emitter, ulong.MaxValue,
            1, Key(5).KeyBytes, recipient, relayer);
        var chain = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(chain, 2);
        var sequence = Enumerable.Repeat((byte)255, 8).ToArray();
        Assert.Equal(WttSolanaAccounts.Derive(token, emitter, chain, sequence).Key, instruction.Keys[3].PublicKey);
        Assert.Equal(recipient.Key, instruction.Keys[5].PublicKey);
        Assert.Equal(relayer.Key, instruction.Keys[6].PublicKey);
        Assert.Single(instruction.Keys.Where(key => key.IsSigner));
        Assert.Throws<ArgumentException>(() => WttSolanaAccounts.Register(token, core, Key(3), Key(4), 2, emitter, 1, 8, Key(5).KeyBytes));
    }
}
