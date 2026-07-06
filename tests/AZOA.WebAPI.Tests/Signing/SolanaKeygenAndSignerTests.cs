using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;
using Solnet.Programs;
using Xunit;
using SolanaAccount = Solnet.Wallet.Account;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// Crypto correctness for final-hardening B1: real Solana keygen (round-trips
/// through Solnet.Wallet) and the real SolanaTransactionSigner (keygen → sign →
/// on-curve verify against the public key). The prior stub-era test
/// (SolanaSignerStubTests) is superseded.
/// </summary>
public class SolanaKeygenAndSignerTests
{
    private const string DummyBlockhash = "9ZNTfG4NyQgxy2SWjSiQoUyBPEvXT2xo7fKc5hPYYJ7b";

    private static WalletKeyService NewKeyService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = "unit-test-wallet-encryption-key-0123456789",
            })
            .Build();
        return new WalletKeyService(config);
    }

    // ─── Keygen ───

    [Fact]
    public void GenerateSolanaKeypair_produces_real_64_byte_secret_and_base58_address()
    {
        var svc = NewKeyService();

        var (publicKeyHex, privateKeyHex, address, seedPhrase) = svc.GenerateKeypair("solana");

        Convert.FromHexString(privateKeyHex).Should().HaveCount(64, "a Solana secret is 32-byte seed ++ 32-byte pubkey");
        Convert.FromHexString(publicKeyHex).Should().HaveCount(32, "an Ed25519 public key is 32 bytes");
        seedPhrase!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(12, "BIP39 12-word mnemonic");
        // The address must be the real base58 of the public key.
        new Solnet.Wallet.PublicKey(address).KeyBytes.Should().Equal(Convert.FromHexString(publicKeyHex));
    }

    [Fact]
    public void GenerateSolanaKeypair_secret_reconstructs_to_the_same_public_key()
    {
        var svc = NewKeyService();
        var (publicKeyHex, privateKeyHex, address, _) = svc.GenerateKeypair("solana");

        var secret = Convert.FromHexString(privateKeyHex);
        var pub = new byte[32];
        Array.Copy(secret, 32, pub, 0, 32);
        var reconstructed = new SolanaAccount(secret, pub);

        reconstructed.PublicKey.Key.Should().Be(address, "the signer reconstructs from this exact representation");
        Convert.ToHexString(reconstructed.PublicKey.KeyBytes).ToLowerInvariant().Should().Be(publicKeyHex);
    }

    [Fact]
    public void GenerateSolanaKeypair_mnemonic_reimports_to_the_same_address()
    {
        var svc = NewKeyService();
        var (_, _, address, seedPhrase) = svc.GenerateKeypair("solana");

        var wallet = new Solnet.Wallet.Wallet(seedPhrase!, Solnet.Wallet.Bip39.WordList.English);
        wallet.Account.PublicKey.Key.Should().Be(address, "re-importing the mnemonic reproduces the same address");
    }

    [Fact]
    public void AesGcm_envelope_round_trips_the_solana_secret()
    {
        var svc = NewKeyService();
        var (_, privateKeyHex, _, seedPhrase) = svc.GenerateKeypair("solana");

        var enc = svc.EncryptPrivateKey(privateKeyHex);
        enc.Should().NotBe(privateKeyHex);
        svc.DecryptPrivateKey(enc).Should().Be(privateKeyHex);
        svc.DecryptSeedPhrase(svc.EncryptSeedPhrase(seedPhrase!)).Should().Be(seedPhrase);
    }

    // ─── Signer ───

    private static byte[] BuildTransferMessage(SolanaAccount from, SolanaAccount to) =>
        new TransactionBuilder()
            .SetRecentBlockHash(DummyBlockhash)
            .SetFeePayer(from)
            .AddInstruction(SystemProgram.Transfer(from.PublicKey, to.PublicKey, 1_000_000))
            .CompileMessage();

    [Fact]
    public void Sign_round_trip_produces_a_verifiable_signature_for_the_signer_public_key()
    {
        var svc = NewKeyService();
        var (_, privateKeyHex, _, _) = svc.GenerateKeypair("solana");
        var secret = Convert.FromHexString(privateKeyHex);
        var pub = new byte[32];
        Array.Copy(secret, 32, pub, 0, 32);
        var from = new SolanaAccount(secret, pub);
        var to = new SolanaAccount();

        var messageBytes = BuildTransferMessage(from, to);
        using var key = new SigningKeyMaterial(secret);

        var result = new SolanaTransactionSigner().Sign(messageBytes, key);

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().NotBeNull();

        // The submittable wire transaction must verify, and the embedded signature
        // must verify against the signer's public key over the canonical message.
        var tx = Transaction.Deserialize(result.Result!);
        tx.VerifySignatures().Should().BeTrue("the assembled Solana transaction must be valid");
    }

    [Fact]
    public void Sign_output_byte_matches_Solnet_reference_build()
    {
        var svc = NewKeyService();
        var (_, privateKeyHex, _, _) = svc.GenerateKeypair("solana");
        var secret = Convert.FromHexString(privateKeyHex);
        var pub = new byte[32];
        Array.Copy(secret, 32, pub, 0, 32);
        var from = new SolanaAccount(secret, pub);
        var to = new SolanaAccount();

        // Reference: Solnet builds + signs the same transaction end to end.
        byte[] reference = new TransactionBuilder()
            .SetRecentBlockHash(DummyBlockhash)
            .SetFeePayer(from)
            .AddInstruction(SystemProgram.Transfer(from.PublicKey, to.PublicKey, 1_000_000))
            .Build(from);

        // Under test: compile the message, hand it to the signer.
        var messageBytes = BuildTransferMessage(from, to);
        using var key = new SigningKeyMaterial(secret);
        var result = new SolanaTransactionSigner().Sign(messageBytes, key);

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Equal(reference,
            "the signer must produce the exact canonical signed wire transaction Solnet does");
    }

    [Fact]
    public void Sign_fails_closed_on_wrong_length_key()
    {
        var signer = new SolanaTransactionSigner();
        using var key = new SigningKeyMaterial(new byte[32]); // Algorand-sized, not 64

        var result = signer.Sign(new byte[] { 1, 2, 3 }, key);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeNull();
        result.Message.Should().Contain("64 bytes");
    }

    [Fact]
    public void Sign_fails_closed_when_key_is_not_the_transaction_signer()
    {
        var svc = NewKeyService();
        // Build a message whose fee payer is account A...
        var (_, pkAHex, _, _) = svc.GenerateKeypair("solana");
        var secretA = Convert.FromHexString(pkAHex);
        var pubA = new byte[32]; Array.Copy(secretA, 32, pubA, 0, 32);
        var accountA = new SolanaAccount(secretA, pubA);
        var to = new SolanaAccount();
        var messageBytes = BuildTransferMessage(accountA, to);

        // ...but hand the signer account B's key.
        var (_, pkBHex, _, _) = svc.GenerateKeypair("solana");
        using var keyB = new SigningKeyMaterial(Convert.FromHexString(pkBHex));

        var result = new SolanaTransactionSigner().Sign(messageBytes, keyB);

        result.IsError.Should().BeTrue("signing with a key that is not the required signer must fail closed");
        result.Message.Should().Contain("required signer");
    }

    [Fact]
    public void Sign_rejects_empty_inputs()
    {
        var signer = new SolanaTransactionSigner();
        using var key = new SigningKeyMaterial(new byte[64]);

        signer.Sign(Array.Empty<byte>(), key).IsError.Should().BeTrue();
    }

    // ─── Factory still resolves both signers (seam is chain-agnostic) ───

    [Fact]
    public void Factory_resolves_both_signers_case_insensitively()
    {
        var factory = new TransactionSignerFactory(new ITransactionSigner[]
        {
            new AlgorandTransactionSigner(),
            new SolanaTransactionSigner(),
        });

        factory.GetSigner("algorand").Should().BeOfType<AlgorandTransactionSigner>();
        factory.GetSigner("SOLANA").Should().BeOfType<SolanaTransactionSigner>();
    }
}
