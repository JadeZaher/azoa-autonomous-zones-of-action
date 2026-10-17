using System.Numerics;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Util;
using Xunit;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// Crypto correctness for Ethereum keygen (chain-value-routes H1): a generated wallet's
/// stored address must be the address its stored private key actually controls.
/// <para>The prior placeholder derived the "public key" with HMAC-SHA256 and the "address"
/// with SHA-256, so a transfer signed with the wallet's real key would have moved value
/// from a different account than the one recorded. The sign-then-recover round trip below
/// is what pins that shut — a unit test of the hash function alone would not have caught
/// the original defect, because the defect was that the address and the key were unrelated.</para>
/// <para>The known-answer vectors are published constants (the secp256k1 generator point,
/// keccak-256 of the empty input, and the address of the scalar 1), not values produced by
/// the code under test.</para>
/// </summary>
public class EthereumKeygenTests
{
    private const string SepoliaChainId = "11155111";
    private const string Recipient = "0x5B38Da6a701c568545dCfcB03FcB875f56beddC4";

    /// <summary>The secp256k1 generator point G (SEC 2 §2.4.3) — the public key of the scalar 1.</summary>
    private const string GeneratorX = "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
    private const string GeneratorY = "483ada7726a3c4655da4fbfc0e1108a8fd17b448a68554199c47d08ffb10d4b8";

    /// <summary>
    /// keccak-256 of the empty input — Ethereum's published EMPTY hash. SHA3-256 of the
    /// same input is a7ffc6f8bf1ed76651c14756a061d662f580ff4de43b49fa82d80a4b80f8434a, so
    /// this constant is what distinguishes real keccak from the NIST-padded SHA-3.
    /// </summary>
    private const string KeccakOfEmptyInput = "c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";

    /// <summary>The published address controlled by the private scalar 1.</summary>
    private const string ScalarOneAddress = "0x7e5f4552091a69125d5dfcb7b8c2659029395bdf";

    /// <summary>Mastering Ethereum ch. 4 worked example: this key controls this address.</summary>
    private const string MasteringEthereumKey = "f8f8a2f43c8376ccb0871305060d7b27b0554d2cc72bccf41b2705608452f315";
    private const string MasteringEthereumAddress = "0x001d3f1ef827552ae1114027bd3ecf1f086ba0f9";

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

    /// <summary>The scalar 1, in the full 32-byte big-endian width a key must keep.</summary>
    private static byte[] ScalarOne()
    {
        var scalar = new byte[WalletKeyService.Secp256k1PrivateKeyLength];
        scalar[^1] = 1;
        return scalar;
    }

    // ─── Derivation ───

    [Fact]
    public void Generated_keypair_is_a_32_byte_scalar_and_an_uncompressed_public_key()
    {
        var (publicKeyHex, privateKeyHex, address, seedPhrase) = NewKeyService().GenerateKeypair("ethereum");

        var privateKey = Convert.FromHexString(privateKeyHex);
        var publicKey = Convert.FromHexString(publicKeyHex);

        privateKey.Should().HaveCount(WalletKeyService.Secp256k1PrivateKeyLength,
            "the signer fails closed on anything that is not a raw 32-byte secp256k1 scalar");
        publicKey.Should().HaveCount(WalletKeyService.Secp256k1UncompressedPublicKeyLength);
        publicKey[0].Should().Be(0x04, "an uncompressed secp256k1 public key is 0x04 followed by X and Y");
        address.Should().MatchRegex("^0x[0-9a-fA-F]{40}$");

        // No BIP-39/BIP-44 derivation is available here, so keygen promises no phrase
        // rather than handing back one that restores nothing.
        seedPhrase.Should().BeNull();
    }

    [Fact]
    public void Known_scalar_one_derives_the_published_generator_point()
    {
        // The public key of the scalar 1 IS the generator point. Matching the published
        // constant proves the derivation is real secp256k1 point multiplication rather
        // than a hash of the private key.
        var (publicKey, _) = WalletKeyService.DeriveEthereumPublicKeyAndAddress(ScalarOne());

        Convert.ToHexStringLower(publicKey).Should().Be("04" + GeneratorX + GeneratorY);
    }

    [Fact]
    public void Known_scalar_one_derives_its_published_ethereum_address()
    {
        var (_, address) = WalletKeyService.DeriveEthereumPublicKeyAndAddress(ScalarOne());

        EthereumAddress.AreSame(address, ScalarOneAddress).Should().BeTrue(
            "the scalar 1 controls the published address {0}, whatever this code computes", ScalarOneAddress);
    }

    [Fact]
    public void Known_mastering_ethereum_vector_derives_its_published_address()
    {
        var (_, address) = WalletKeyService.DeriveEthereumPublicKeyAndAddress(
            Convert.FromHexString(MasteringEthereumKey));

        EthereumAddress.AreSame(address, MasteringEthereumAddress).Should().BeTrue();
    }

    [Fact]
    public void Address_is_the_last_twenty_bytes_of_keccak_over_the_public_key_without_its_prefix()
    {
        // Pin that the hash in play really is keccak-256 and not SHA3-256 or SHA-256,
        // against the published empty-input digest...
        var keccak = new Sha3Keccack();
        Convert.ToHexStringLower(keccak.CalculateHash(Array.Empty<byte>())).Should().Be(KeccakOfEmptyInput);

        // ...then recompute the address the long way from the public key alone.
        var (publicKeyHex, _, address, _) = NewKeyService().GenerateKeypair("ethereum");
        var publicKey = Convert.FromHexString(publicKeyHex);
        var expected = "0x" + Convert.ToHexStringLower(keccak.CalculateHash(publicKey[1..])[^20..]);

        EthereumAddress.AreSame(address, expected).Should().BeTrue(
            "address = keccak256(uncompressedPublicKey[1..])[12..]");
    }

    [Fact]
    public void Generated_address_is_already_in_eip55_checksummed_form()
    {
        var (_, _, address, _) = NewKeyService().GenerateKeypair("ethereum");

        // A mixed-case address that fails its checksum is REJECTED by the provider, so a
        // stored address must be in exactly the casing EIP-55 prescribes.
        EthereumAddress.TryParse(address, out var checksummed).Should().BeTrue();
        checksummed.Should().Be(address);
    }

    // ─── The round trip that proves the address belongs to the key ───

    [Fact]
    public void Generated_address_is_the_sender_a_transaction_signed_with_the_generated_key_recovers_to()
    {
        var (_, privateKeyHex, address, _) = NewKeyService().GenerateKeypair("ethereum");

        var sender = SignAndRecoverSender(Convert.FromHexString(privateKeyHex));

        EthereumAddress.AreSame(sender, address).Should().BeTrue(
            "value signed with the wallet's key must leave the wallet's own recorded address");
    }

    [Fact]
    public void Stored_key_still_signs_as_the_stored_address_after_the_encrypt_decrypt_round_trip()
    {
        // The custody hot path resolves keys through DecryptPrivateKeyBytes, so the shape
        // that survives storage — not just the shape keygen returned — must sign as the
        // recorded address.
        var service = NewKeyService();
        var (_, privateKeyHex, address, _) = service.GenerateKeypair("ethereum");

        var resolved = service.DecryptPrivateKeyBytes(service.EncryptPrivateKey(privateKeyHex));

        resolved.Should().HaveCount(WalletKeyService.Secp256k1PrivateKeyLength);
        EthereumAddress.AreSame(SignAndRecoverSender(resolved), address).Should().BeTrue();
    }

    [Fact]
    public void A_scalar_with_leading_zero_bytes_keeps_its_full_width_and_signs_as_its_own_address()
    {
        // The scalar 1 is 31 leading zero bytes. An unsigned big-integer re-encoding would
        // shrink it to a single byte, which the signer refuses — one draw in 256 would
        // otherwise produce a wallet that could never spend.
        var scalarOne = ScalarOne();
        var (_, address) = WalletKeyService.DeriveEthereumPublicKeyAndAddress(scalarOne);

        EthereumAddress.AreSame(SignAndRecoverSender(scalarOne), address).Should().BeTrue();
    }

    // ─── The provider's own gates accept what keygen produced ───

    [Fact]
    public async Task Generated_address_passes_the_providers_own_eip55_validation()
    {
        var (_, _, address, _) = NewKeyService().GenerateKeypair("ethereum");
        var provider = new EthereumProvider(ProviderConfig(), NullLogger<EthereumProvider>.Instance);

        var validated = await provider.ValidateAddressAsync(address);

        validated.IsError.Should().BeFalse(validated.Message);
        validated.Result.Should().BeTrue();
    }

    [Fact]
    public async Task The_stub_era_address_shape_would_not_have_passed_that_validation_unchecked()
    {
        // Guard the guard: ValidateAddressAsync must actually reject a bad address, or the
        // test above would pass for a wallet whose address was never derived from its key.
        var provider = new EthereumProvider(ProviderConfig(), NullLogger<EthereumProvider>.Instance);

        var validated = await provider.ValidateAddressAsync("0xAAe5F4552091a69125d5dfcb7b8c2659029395bdf");

        validated.Result.Should().BeFalse("a mixed-case address that fails EIP-55 is a typo, not an account");
    }

    // ─── Randomness ───

    [Fact]
    public void Two_generations_produce_unrelated_keypairs()
    {
        var service = NewKeyService();

        var first = service.GenerateKeypair("ethereum");
        var second = service.GenerateKeypair("ethereum");

        second.privateKeyHex.Should().NotBe(first.privateKeyHex, "keygen must not run off a fixed seed");
        second.publicKey.Should().NotBe(first.publicKey);
        second.address.Should().NotBe(first.address);
    }

    // ─── Helpers ───

    private static IConfiguration ProviderConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "devnet",
                ["Blockchain:Chains:0:ChainType"] = "Ethereum",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://ethereum.test/",
                ["Blockchain:Chains:0:Devnet:ChainId"] = SepoliaChainId,
            })
            .Build();

    private static Transaction1559 NewTransaction() => new(
        BigInteger.Parse(SepoliaChainId),
        nonce: 7,
        maxPriorityFeePerGas: 1_500_000_000,
        maxFeePerGas: 30_000_000_000,
        gasLimit: 21_000,
        receiverAddress: Recipient,
        amount: 1_000_000,
        data: "0x",
        accessList: null);

    /// <summary>Signs a real EIP-1559 transfer with <paramref name="privateKey"/> and returns the recovered sender.</summary>
    private static string SignAndRecoverSender(byte[] privateKey)
    {
        using var material = new SigningKeyMaterial(privateKey);

        var signed = new EthereumTransactionSigner().Sign(NewTransaction().GetRLPEncodedRaw(), material);

        signed.IsError.Should().BeFalse(signed.Message);
        var decoded = Transaction1559Encoder.Current.Decode(signed.Result!);
        TransactionVerificationAndRecovery.VerifyTransaction(decoded).Should().BeTrue();
        return TransactionVerificationAndRecovery.GetSenderAddress(decoded);
    }
}
