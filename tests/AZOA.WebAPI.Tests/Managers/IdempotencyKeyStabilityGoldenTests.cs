// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using System.Reflection;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// Pins <c>BlockchainOperationManager.DeriveIdempotencyKey</c> byte-for-byte across the
/// D13 widening of <see cref="IMintOperation.Amount"/> from <c>ulong</c> to
/// <see cref="RailAmount"/>.
///
/// <para><b>Why this file exists.</b> The key is a SHA-256 over the operation's
/// value-bearing inputs rendered as strings. The Mint branch used to read
/// <c>ulong.ToString()</c>; a <see cref="RailAmount"/> renders as
/// <c>"1000000 base units"</c> from its own <c>ToString()</c>, so the obvious mechanical
/// port would have silently re-hashed every key. An operation already claimed under the
/// old spelling would then no longer match its own key, replay protection would stop
/// recognising duplicates, and an irreversible chain effect could run twice. The fix is
/// <c>ToBaseUnitString()</c>; this file is the proof that it is equivalent.</para>
///
/// <para><b>What the goldens are.</b> The expected digests are NOT captured from a build
/// of this code. They are computed independently from the pre-change specification —
/// SHA-256 over <c>Uri.EscapeDataString</c>-escaped components joined with <c>|</c>, with
/// the amount rendered by <c>ulong.ToString()</c> — so the assertion is against the old
/// contract rather than against whatever the new code happens to produce.</para>
///
/// <para>Reflection is used deliberately: the derivation is a private implementation
/// detail and should stay one, but its OUTPUT is a durable cross-process contract, so it
/// has to be pinned somewhere.</para>
/// </summary>
public sealed class IdempotencyKeyStabilityGoldenTests
{
    private static readonly MethodInfo DeriveIdempotencyKey =
        typeof(BlockchainOperationManager).GetMethod(
            "DeriveIdempotencyKey",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "BlockchainOperationManager.DeriveIdempotencyKey is gone or changed shape — "
            + "the idempotency key is a durable contract and cannot be renamed silently.");

    private static string Derive(IBlockchainOperation operation, string chainType, ChainNetwork network)
        => (string)DeriveIdempotencyKey.Invoke(null, [operation, chainType, network])!;

    // ── Goldens: computed from the pre-widening (ulong.ToString) specification ──

    [Theory]
    [InlineData("1", "ipfs://token-uri", "NFT", "Algorand", ChainNetwork.Devnet, "ALGOWALLET1",
        "op_algorand_mint_c31f717ec9b319e419188f33aff72fc4d93aa3a035518c57d2014b8df19f990d")]
    [InlineData("0", "", "", "Algorand", ChainNetwork.Devnet, "ALGOWALLET1",
        "op_algorand_mint_4e96f9c34a694122beacdbfc9ca62ed94ef4c19c677298f47cbc7bdeb68a4475")]
    [InlineData("1000000", "ipfs://token-uri", "FT", "Algorand", ChainNetwork.Devnet, "ALGOWALLET1",
        "op_algorand_mint_4d1dd181b718826055125852b86e8db3d72747570e614f6e7b8d1e6588dfcef3")]
    [InlineData("18446744073709551615", "ipfs://big", "ERC20", "Ethereum", ChainNetwork.Mainnet, "0xabc",
        "op_ethereum_mint_d96506c274baf7d29c5849662fb31f6a8c343776f130f1cef86012475f9440b3")]
    public void MintKey_MatchesThePreWideningGolden(
        string baseUnits,
        string tokenUri,
        string assetType,
        string chainType,
        ChainNetwork network,
        string walletAddress,
        string expectedKey)
    {
        var operation = new BlockchainOperation
        {
            OperationType = "Mint",
            TokenUri = tokenUri,
            AssetType = assetType,
            Amount = RailAmount.OfChainAsset(BigInteger.Parse(baseUnits, CultureInfo.InvariantCulture)),
            Parameters = new Dictionary<string, string> { ["WalletAddress"] = walletAddress },
        };

        Derive(operation, chainType, network).Should().Be(
            expectedKey,
            "a settled operation must still hash to the key it was claimed under");
    }

    [Fact]
    public void MintKey_WithAParentKey_MatchesThePreWideningGolden()
    {
        var operation = new BlockchainOperation
        {
            OperationType = "Mint",
            TokenUri = "ipfs://token-uri",
            AssetType = "NFT",
            Amount = RailAmount.OfChainAsset(7),
            Parameters = new Dictionary<string, string>
            {
                ["WalletAddress"] = "ALGOWALLET1",
                ["IdempotencyKey"] = "parent-key-1",
            },
        };

        Derive(operation, "Algorand", ChainNetwork.Testnet).Should().Be(
            "op_algorand_mint_5c78dbf1bccf72e92790140821f3ea8853445303f8a996152ba7fd707dfc9bb8");
    }

    /// <summary>
    /// Burn and Transfer read <c>Parameters["Amount"]</c> as a string and were never
    /// capped; they are pinned here so the widening is shown not to have disturbed the
    /// branches it did not touch.
    /// </summary>
    [Fact]
    public void BurnKey_IsUnchanged()
    {
        var operation = new BlockchainOperation
        {
            OperationType = "Burn",
            Parameters = new Dictionary<string, string>
            {
                ["WalletAddress"] = "ALGOWALLET1",
                ["TokenId"] = "555",
                ["Amount"] = "5",
            },
        };

        Derive(operation, "Algorand", ChainNetwork.Devnet).Should().Be(
            "op_algorand_burn_4957a6164a9061f6b9632c16353535a8546f3fbef3be5fe5ee1baf02aedd8e74");
    }

    [Fact]
    public void TransferKey_WithAnAmountFarAboveUlongMax_IsUnchanged()
    {
        var operation = new BlockchainOperation
        {
            OperationType = "Transfer",
            RecipientAddress = "0xdef",
            Parameters = new Dictionary<string, string>
            {
                ["WalletAddress"] = "0xabc",
                ["SourceTokenId"] = "555",
                ["Amount"] = "340282366920938463463374607431768211456",
            },
        };

        Derive(operation, "Ethereum", ChainNetwork.Mainnet).Should().Be(
            "op_ethereum_transfer_da6f412025b0ab9166468f0637bc68b6f33f99bc2237986735452c81d5678136");
    }

    // ── The equivalence the goldens rest on ────────────────────────────────────

    /// <summary>
    /// <c>RailAmount.ToBaseUnitString()</c> and the retired <c>ulong.ToString()</c> agree
    /// on every value the old type could hold: both emit plain invariant digits with no
    /// sign, no group separators and no suffix. This is why swapping the projection is
    /// key-preserving rather than key-breaking — and why <c>RailAmount.ToString()</c>,
    /// which appends the dimension, would not have been.
    /// </summary>
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(999UL)]
    [InlineData(1_000_000UL)]
    [InlineData(2_147_483_647UL)]
    [InlineData(9_223_372_036_854_775_807UL)]
    [InlineData(ulong.MaxValue)]
    public void ToBaseUnitString_IsByteIdenticalToTheRetiredUlongToString(ulong value)
    {
        RailAmount.OfChainAsset(value).ToBaseUnitString().Should().Be(value.ToString());
    }

    [Fact]
    public void RailAmountToString_WouldHaveChangedTheKey_WhichIsWhyItIsNotUsed()
    {
        // Guards the comment on the Mint branch: if RailAmount ever starts rendering as
        // bare digits this test fails and the comment stops being load-bearing.
        RailAmount.OfChainAsset(1_000_000).ToString()
            .Should().NotBe("1000000",
                "RailAmount.ToString() carries the dimension; only ToBaseUnitString() is "
                + "the key-stable projection");
    }

    /// <summary>
    /// The widening's actual point, at the key level: two amounts that both sit above the
    /// 64-bit line and differ only there must still derive different keys. Under the old
    /// <c>ulong</c> field neither amount could be represented at all.
    /// </summary>
    [Fact]
    public void MintKeys_AboveUlongMax_StayDistinct()
    {
        var lower = MintOpWith(BigInteger.Parse("18446744073709551616"));   // 2^64
        var higher = MintOpWith(BigInteger.Parse("18446744073709551617"));  // 2^64 + 1

        Derive(lower, "Ethereum", ChainNetwork.Mainnet)
            .Should().NotBe(Derive(higher, "Ethereum", ChainNetwork.Mainnet));
    }

    private static BlockchainOperation MintOpWith(BigInteger baseUnits) => new()
    {
        OperationType = "Mint",
        TokenUri = "ipfs://big",
        AssetType = "ERC20",
        Amount = RailAmount.OfChainAsset(baseUnits),
        Parameters = new Dictionary<string, string> { ["WalletAddress"] = "0xabc" },
    };
}
