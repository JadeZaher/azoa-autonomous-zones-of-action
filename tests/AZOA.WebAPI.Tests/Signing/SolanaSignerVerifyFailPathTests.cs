using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Programs;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;
using Account = Solnet.Wallet.Account;

namespace AZOA.WebAPI.Tests.Signing;

public class SolanaSignerVerifyFailPathTests
{
    private const string Blockhash = "9ZNTfG4NyQgxy2SWjSiQoUyBPEvXT2xo7fKc5hPYYJ7b";

    [Fact]
    public void SingleSigner_RejectsTrailingBytesInsteadOfNormalizingUnsignedContent()
    {
        var account = new Account(); var recipient = new Account();
        var message = new TransactionBuilder().SetRecentBlockHash(Blockhash).SetFeePayer(account)
            .AddInstruction(SystemProgram.Transfer(account.PublicKey, recipient.PublicKey, 1)).CompileMessage();
        using var material = new SigningKeyMaterial((byte[])account.PrivateKey.KeyBytes.Clone());
        var signer = new SolanaTransactionSigner();
        var signed = signer.Sign(message, material);
        signed.IsError.Should().BeFalse(signed.Message);
        Transaction.Deserialize(signed.Result!).VerifySignatures().Should().BeTrue();
        signer.Sign(message.Concat(new byte[] { 0 }).ToArray(), material).IsError.Should().BeTrue();
    }

    [Fact]
    public void DetachedSignatures_RequireEachDeclaredKeyAndExactMessage()
    {
        var payer = new Account(); var issuer = new Account(); var stranger = new Account();
        var message = new TransactionBuilder().SetRecentBlockHash(Blockhash).SetFeePayer(payer)
            .AddInstruction(SystemProgram.Transfer(payer.PublicKey, stranger.PublicKey, 1))
            .AddInstruction(MemoProgram.NewMemoV2("issuer-consent", issuer.PublicKey)).CompileMessage();
        using var payerKey = new SigningKeyMaterial((byte[])payer.PrivateKey.KeyBytes.Clone());
        using var issuerKey = new SigningKeyMaterial((byte[])issuer.PrivateKey.KeyBytes.Clone());
        var signer = new SolanaTransactionSigner();
        signer.Sign(message, payerKey).IsError.Should().BeTrue();
        signer.SignPartial(message, issuer.PublicKey.Key, payerKey).IsError.Should().BeTrue();
        signer.SignPartial(message, stranger.PublicKey.Key, issuerKey).IsError.Should().BeTrue();
        var payerSig = signer.SignPartial(message, payer.PublicKey.Key, payerKey);
        var issuerSig = signer.SignPartial(message, issuer.PublicKey.Key, issuerKey);
        payerSig.IsError.Should().BeFalse(payerSig.Message); issuerSig.IsError.Should().BeFalse(issuerSig.Message);
        var parsed = Message.Deserialize(message);
        var byKey = new Dictionary<string, byte[]> { [payer.PublicKey.Key] = payerSig.Result!, [issuer.PublicKey.Key] = issuerSig.Result! };
        var transaction = Transaction.Populate(parsed, parsed.AccountKeys.Take(parsed.Header.RequiredSignatures).Select(key => byKey[key.Key]).ToList());
        transaction.VerifySignatures().Should().BeTrue();
        var tampered = (byte[])message.Clone(); tampered[^1] ^= 1;
        issuer.PublicKey.Verify(tampered, issuerSig.Result!).Should().BeFalse();
    }
}
