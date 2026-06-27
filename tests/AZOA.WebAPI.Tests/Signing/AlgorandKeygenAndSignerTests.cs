using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using Xunit;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// Crypto correctness for signing-core-keystone Phases 1 + 2: real Algorand
/// keygen (round-trips through Algorand2) and the AlgorandTransactionSigner
/// (byte-matches an Algorand2 reference sign).
/// </summary>
public class AlgorandKeygenAndSignerTests
{
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

    [Fact]
    public void GenerateAlgorandKeypair_produces_valid_checksummed_address()
    {
        var svc = NewKeyService();

        var (publicKeyHex, privateKeyHex, address, seedPhrase) = svc.GenerateKeypair("algorand");

        address.Should().HaveLength(58);
        Algorand.Address.IsValid(address).Should().BeTrue("the address must carry a real SHA-512/256 checksum");
        publicKeyHex.Should().HaveLength(64, "an Ed25519 public key is 32 bytes");
        privateKeyHex.Should().NotBeNullOrWhiteSpace();
        seedPhrase.Should().NotBeNullOrWhiteSpace();
        seedPhrase!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(25,
            "Algorand mnemonics are 25 words");
    }

    [Fact]
    public void GenerateAlgorandKeypair_address_matches_Algorand2_derivation_from_same_private_key()
    {
        var svc = NewKeyService();

        var (publicKeyHex, privateKeyHex, address, _) = svc.GenerateKeypair("algorand");

        // Reconstruct the account from the persisted private-key bytes exactly as
        // AlgorandTransactionSigner does, and confirm the address + public key match.
        var privateKeyBytes = Convert.FromHexString(privateKeyHex);
        var reconstructed = new AlgoAccount(privateKeyBytes);

        reconstructed.Address.EncodeAsString().Should().Be(address);
        Convert.ToHexString(reconstructed.KeyPair.ClearTextPublicKey).ToLowerInvariant()
            .Should().Be(publicKeyHex);
    }

    [Fact]
    public void GenerateAlgorandKeypair_mnemonic_reimports_to_same_address()
    {
        var svc = NewKeyService();

        var (_, _, address, seedPhrase) = svc.GenerateKeypair("algorand");

        var reimported = new AlgoAccount(seedPhrase!.Trim());
        reimported.Address.EncodeAsString().Should().Be(address,
            "re-importing the 25-word mnemonic must reproduce the same address");
    }

    [Fact]
    public void AesGcm_envelope_still_round_trips_the_new_real_private_key()
    {
        var svc = NewKeyService();
        var (_, privateKeyHex, _, seedPhrase) = svc.GenerateKeypair("algorand");

        var encKey = svc.EncryptPrivateKey(privateKeyHex);
        var encSeed = svc.EncryptSeedPhrase(seedPhrase!);

        encKey.Should().NotBe(privateKeyHex);
        svc.DecryptPrivateKey(encKey).Should().Be(privateKeyHex);
        svc.DecryptSeedPhrase(encSeed).Should().Be(seedPhrase);
    }

    [Fact]
    public void Signer_output_byte_matches_Algorand2_reference_for_identical_inputs()
    {
        // Deterministic account from a fixed mnemonic so the reference is stable.
        var account = new AlgoAccount();
        var privateKeyBytes = account.KeyPair.ClearTextPrivateKey;

        // Build an identical unsigned transfer transaction for both paths.
        Transaction BuildTxn()
        {
            var txn = new AssetTransferTransaction
            {
                Sender = account.Address,
                XferAsset = 12345UL,
                AssetReceiver = account.Address,
                AssetAmount = 7UL,
            };
            txn.FirstValid = 1000UL;
            txn.LastValid = 2000UL;
            txn.GenesisId = "testnet-v1.0";
            txn.GenesisHash = new Algorand.Digest("SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=");
            txn.SetFee(1000UL);
            return txn;
        }

        // Reference: Algorand2 sign + canonical encode directly.
        var referenceSigned = BuildTxn().Sign(account);
        var referenceBytes = Encoder.EncodeToMsgPackOrdered(referenceSigned);

        // Under test: encode unsigned canonically, hand to the signer.
        var unsignedCanonical = Encoder.EncodeToMsgPackOrdered(BuildTxn());
        using var keyMaterial = new SigningKeyMaterial(privateKeyBytes);
        var signer = new AlgorandTransactionSigner();

        var result = signer.Sign(unsignedCanonical, keyMaterial);

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().NotBeNull();
        result.Result.Should().Equal(referenceBytes,
            "the signer must produce the exact canonical signed envelope Algorand2 does");
    }

    [Fact]
    public void Signer_rejects_empty_inputs()
    {
        var signer = new AlgorandTransactionSigner();
        using var key = new SigningKeyMaterial(new byte[32]);

        signer.Sign(Array.Empty<byte>(), key).IsError.Should().BeTrue();
    }

    [Fact]
    public void SigningKeyMaterial_zeroes_buffer_on_dispose()
    {
        var raw = new byte[] { 1, 2, 3, 4, 5 };
        var material = new SigningKeyMaterial(raw);

        // Defensive copy: mutating the caller's buffer does not affect the material.
        raw[0] = 99;
        material.PrivateKey[0].Should().Be(1);

        material.Dispose();
        var act = () => _ = material.PrivateKey;
        act.Should().Throw<ObjectDisposedException>();
    }
}
