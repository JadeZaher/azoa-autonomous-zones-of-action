using System.Security.Cryptography;
using AZOA.WebAPI.Services.Custody;
using FluentAssertions;
using Solnet.Wallet.Bip39;
using AlgoAccount = Algorand.Algod.Model.Account;
using SolanaWallet = Solnet.Wallet.Wallet;

namespace AZOA.WebAPI.Tests.Custody;

/// <summary>
/// Per-chain platform key derivation (<c>Services/Custody/AGENTS.md</c>
/// §platform-key-derivation). Each chain must produce ITS OWN key shape from ITS OWN
/// secret, a malformed secret must fail without echoing anything, and an unregistered
/// chain must resolve to nothing rather than to a neighbour's rule.
/// </summary>
public sealed class PlatformKeyDerivationTests
{
    /// <summary>Well-known Ethereum documentation test key; deterministic, worthless.</summary>
    private const string EthereumTestPrivateKey =
        "0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";

    private static string NewAlgorandMnemonic() => new AlgoAccount().ToMnemonic();

    private static string NewSolanaMnemonic() => new Mnemonic(WordList.English, WordCount.Twelve).ToString();

    // ─── Correct per-chain derivation, with the REAL shape each signer expects ───

    [Fact]
    public void Algorand_derives_the_32_byte_cleartext_private_key_the_signer_reconstructs()
    {
        var mnemonic = NewAlgorandMnemonic();
        var derivation = new AlgorandPlatformKeyDerivation();

        derivation.TryDerive(mnemonic, out var key).Should().BeTrue();

        key.Length.Should().Be(32, "Algorand2 ClearTextPrivateKey is the 32-byte Ed25519 private scalar");
        key.Should().Equal(new AlgoAccount(mnemonic).KeyPair.ClearTextPrivateKey);
        derivation.KeyLength.Should().Be(32);
        derivation.PlatformKeyConfigPath.Should().Be("AZOA:Algorand:PlatformMnemonic");
    }

    [Fact]
    public void Solana_derives_the_64_byte_solnet_secret_not_an_algorand_seed()
    {
        var mnemonic = NewSolanaMnemonic();
        var derivation = new SolanaPlatformKeyDerivation();

        derivation.TryDerive(mnemonic, out var key).Should().BeTrue();

        key.Length.Should().Be(64, "Solnet PrivateKey.KeyBytes is the 64-byte secret (seed ++ public key)");
        key.Should().Equal(new SolanaWallet(mnemonic, WordList.English).Account.PrivateKey.KeyBytes);
        derivation.KeyLength.Should().Be(64);
        derivation.PlatformKeyConfigPath.Should().Be("AZOA:Solana:PlatformMnemonic");
    }

    [Fact]
    public void Ethereum_derives_the_raw_32_byte_secp256k1_scalar_from_its_own_config_path()
    {
        var derivation = new EthereumPlatformKeyDerivation();

        derivation.TryDerive(EthereumTestPrivateKey, out var key).Should().BeTrue();

        key.Length.Should().Be(32);
        key.Should().Equal(Convert.FromHexString(EthereumTestPrivateKey[2..]));
        derivation.PlatformKeyConfigPath.Should().Be(
            "AZOA:Ethereum:PlatformPrivateKey",
            "an Ethereum platform secret is a raw key, not a mnemonic — a BIP-39 phrase needs a "
            + "BIP-44 path and guessing it yields a valid key for the wrong account");
    }

    [Fact]
    public void Each_chain_derives_a_different_key_shape_from_the_same_kind_of_secret()
    {
        // The defect this whole seam exists for: one mnemonic run through two chains'
        // rules must not be assumed interchangeable.
        var algorand = new AlgorandPlatformKeyDerivation();
        var solana = new SolanaPlatformKeyDerivation();

        algorand.TryDerive(NewAlgorandMnemonic(), out var algorandKey).Should().BeTrue();
        solana.TryDerive(NewSolanaMnemonic(), out var solanaKey).Should().BeTrue();

        algorandKey.Length.Should().NotBe(solanaKey.Length);
    }

    // ─── Malformed secrets fail closed, silently ───

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a mnemonic at all")]
    public void Algorand_rejects_a_malformed_mnemonic_without_throwing(string secret)
    {
        new AlgorandPlatformKeyDerivation().TryDerive(secret, out var key).Should().BeFalse();
        key.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a mnemonic at all")]
    public void Solana_rejects_a_malformed_mnemonic_without_throwing(string secret)
    {
        new SolanaPlatformKeyDerivation().TryDerive(secret, out var key).Should().BeFalse();
        key.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318")] // no 0x prefix
    [InlineData("0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f3623")]  // 31 bytes
    [InlineData("0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f36231800")] // 33 bytes
    [InlineData("0xzzzz83a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318")] // not hex
    [InlineData("0x0000000000000000000000000000000000000000000000000000000000000000")] // invalid scalar
    public void Ethereum_rejects_anything_that_is_not_a_0x_prefixed_valid_scalar(string secret)
    {
        new EthereumPlatformKeyDerivation().TryDerive(secret, out var key).Should().BeFalse();
        key.Should().BeEmpty();
    }

    // ─── Registry: total lookup, no silent neighbour ───

    [Fact]
    public void Default_registry_covers_exactly_the_real_provider_chains_plus_Simulated()
    {
        PlatformKeyDerivationRegistry.Default.RegisteredChainTypes
            .Should().BeEquivalentTo(["Algorand", "Ethereum", "Simulated", "Solana"]);
    }

    [Theory]
    [InlineData("algorand", "Algorand")]
    [InlineData("SOLANA", "Solana")]
    [InlineData("  Ethereum  ", "Ethereum")]
    public void Registry_lookup_is_case_and_whitespace_insensitive(string requested, string expected)
    {
        PlatformKeyDerivationRegistry.Default.TryGet(requested, out var derivation).Should().BeTrue();
        derivation!.ChainType.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bitcoin")]
    public void Registry_resolves_nothing_for_an_unregistered_chain(string? chainType)
    {
        PlatformKeyDerivationRegistry.Default.TryGet(chainType, out var derivation).Should().BeFalse();
        derivation.Should().BeNull();
    }

    [Fact]
    public void Registry_refuses_two_rules_for_one_chain()
    {
        var duplicate = () => new PlatformKeyDerivationRegistry(
        [
            new AlgorandPlatformKeyDerivation(),
            new AlgorandPlatformKeyDerivation(),
        ]);

        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*Algorand*");
    }

    [Fact]
    public void Registered_chain_names_are_safe_to_print_and_carry_no_config_values()
    {
        var display = PlatformKeyDerivationRegistry.Default.RegisteredChainTypesDisplay;

        display.Should().Be("Algorand, Ethereum, Simulated, Solana");
        display.Should().NotContain(":", "a chain list must never carry a config path or value");
    }

    // ─── Derived buffers are the caller's to zero ───

    [Fact]
    public void Derived_key_is_an_independent_buffer_the_caller_can_zero()
    {
        var mnemonic = NewAlgorandMnemonic();
        var derivation = new AlgorandPlatformKeyDerivation();

        derivation.TryDerive(mnemonic, out var first).Should().BeTrue();
        CryptographicOperations.ZeroMemory(first);

        derivation.TryDerive(mnemonic, out var second).Should().BeTrue();
        second.Should().NotBeEquivalentTo(new byte[second.Length], "zeroing one buffer must not poison the source");
    }
}
