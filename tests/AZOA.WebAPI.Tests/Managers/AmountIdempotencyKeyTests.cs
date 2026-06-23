// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// Pins the C-1 regression: the historic <c>ClampToInt</c> path in AllocationManager
/// collapsed any ulong amount above <see cref="int.MaxValue"/> to 2 147 483 647 before
/// it reached <c>BlockchainOperation.Amount</c>. Because <c>DeriveIdempotencyKey</c>
/// reads the typed <c>Amount</c> field, two distinct large allocations (e.g. 5 B and
/// 9 B tokens) derived the SAME idempotency key — the second was silently deduped
/// and never broadcast (silent under-allocation). Widening <c>Amount</c> to <c>ulong</c>
/// end-to-end keeps the true value intact and the keys distinct.
///
/// This file provides a focused old-vs-new contrast test:
///   RED  side — a local model of the historic int-clamp projection proves two distinct
///               amounts above int.MaxValue collapse to one key component (the bug).
///   GREEN side — the current ulong projection keeps them distinct (the fix).
///   REAL  path — <c>ExecuteAsync</c> is driven with both amounts through the live
///               <see cref="BlockchainOperationManager"/> so the actual
///               <c>DeriveIdempotencyKey</c> logic is exercised end-to-end, confirming
///               two records land in the idempotency store (not one).
///
/// The integration angle is also covered by
/// <c>BlockchainOperationManagerTests.ExecuteAsync_DistinctLargeMintAmounts_AboveIntMaxValue_DeriveDistinctKeys</c>;
/// this file's value is the EXPLICIT old-vs-new projection contrast that makes the
/// C-1 root cause immediately readable as a test document.
/// </summary>
public class AmountIdempotencyKeyTests
{
    // ── Two distinct amounts both well above int.MaxValue ───────────────────────
    private const ulong AmountA = 5_000_000_000UL;  // 5 billion
    private const ulong AmountB = 9_000_000_000UL;  // 9 billion

    // ── RED side: the historic int-clamp projection (models the pre-fix bug) ────

    /// <summary>
    /// Reproduces the pre-fix saturating clamp (ulong → int, capped at
    /// <see cref="int.MaxValue"/>). This is the DELIBERATE (long) cast that
    /// encodes the bug: it existed in production so both large amounts landed on
    /// 2 147 483 647 and collided. Do NOT remove or "fix" this helper —
    /// its job is to document the collision.
    /// </summary>
    private static long OldClampModel(ulong amount)
        => amount > (ulong)int.MaxValue
            ? int.MaxValue          // ← the historic saturation (DELIBERATE bug cast)
            : (long)amount;

    // ── GREEN side: the current ulong projection (the fix) ──────────────────────

    private static string NewUlongProjection(ulong amount) => amount.ToString();

    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// C-1 regression pin.
    ///
    /// RED:  under the old int-clamp, 5 000 000 000 and 9 000 000 000 both saturate
    ///       to int.MaxValue — same key component — the second allocation was silently
    ///       deduped (never broadcast → silent under-allocation).
    ///
    /// GREEN: under the current ulong projection the two amounts remain distinct strings
    ///        so they derive distinct idempotency keys and both allocations broadcast.
    /// </summary>
    [Fact]
    public void IdempotencyKey_LargeAmounts_OldIntClampCollides_NewUlongDistinct()
    {
        // Precondition: both amounts are genuinely above int.MaxValue so the
        // saturation actually fires (not a test-setup error).
        AmountA.Should().BeGreaterThan((ulong)int.MaxValue,
            "AmountA must exceed int.MaxValue to trigger the historic saturation");
        AmountB.Should().BeGreaterThan((ulong)int.MaxValue,
            "AmountB must exceed int.MaxValue to trigger the historic saturation");

        // ── RED side ─────────────────────────────────────────────────────────────
        // Under the old int-clamp both distinct amounts collapsed to the same
        // integer → same key component → collision → second op silently deduped.
        var oldA = OldClampModel(AmountA);
        var oldB = OldClampModel(AmountB);

        oldA.Should().Be(int.MaxValue,
            "the historic clamp saturates 5 000 000 000 to int.MaxValue");
        oldB.Should().Be(int.MaxValue,
            "the historic clamp saturates 9 000 000 000 to int.MaxValue");
        oldA.Should().Be(oldB,
            "RED: the old int-clamp collapsed two DISTINCT large amounts to the same " +
            "key component — this is the C-1 collision that caused silent under-allocation");

        // ── GREEN side ───────────────────────────────────────────────────────────
        // Under the current ulong projection each amount keeps its true string
        // representation → distinct key components → both ops broadcast.
        var newA = NewUlongProjection(AmountA);
        var newB = NewUlongProjection(AmountB);

        newA.Should().Be("5000000000");
        newB.Should().Be("9000000000");
        newA.Should().NotBe(newB,
            "GREEN: the ulong projection keeps distinct large amounts as distinct key " +
            "components — no collision, both allocations broadcast");
    }

    // ── REAL path: drive BlockchainOperationManager end-to-end ─────────────────

    private static BlockchainOperationManager BuildManager(
        Mock<IBlockchainProvider> provider,
        FakeIdempotencyStore idempotency)
    {
        var store = new Mock<IBlockchainOperationStore>();
        store.Setup(s => s.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((IBlockchainOperation op, CancellationToken _) =>
                 new AZOAResult<IBlockchainOperation> { Result = op });

        var config = new ConfigurationBuilder().Build();
        var factory = new BlockchainProviderFactory(new[] { provider.Object }, config);
        return new BlockchainOperationManager(store.Object, factory, idempotency);
    }

    private static BlockchainOperation MintOp(ulong amount) => new()
    {
        OperationType = "Mint",
        TokenUri = "ipfs://grant",
        Amount = amount,
        AssetType = "NFT",
        Parameters = new Dictionary<string, string>
        {
            ["ChainType"] = "Algorand",
            ["WalletAddress"] = "ALGOTESTADDRESS"
        }
    };

    /// <summary>
    /// Drives the real <c>DeriveIdempotencyKey</c> path through
    /// <see cref="BlockchainOperationManager.ExecuteAsync"/>.
    ///
    /// Two ops that differ ONLY in a large ulong Amount (both above int.MaxValue)
    /// must land as TWO distinct idempotency records and both broadcast.
    /// Under the pre-fix int-clamp they would have collided to one record
    /// (mintCalls == 1); post-fix they are distinct (mintCalls == 2).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TwoLargeAmounts_AboveIntMax_DeriveDistinctKeys_BothBroadcast()
    {
        var mintCalls = 0;
        var provider = new Mock<IBlockchainProvider>();
        provider.Setup(p => p.ChainType).Returns("Algorand");
        provider.Setup(p => p.MintAsync(
                    It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    Interlocked.Increment(ref mintCalls);
                    return new AZOAResult<string> { Result = "algo_tx_c1" };
                });

        var idempotency = new FakeIdempotencyStore();
        var manager = BuildManager(provider, idempotency);

        var opA = MintOp(AmountA);
        var opB = MintOp(AmountB);

        var rA = await manager.ExecuteAsync(opA);
        var rB = await manager.ExecuteAsync(opB);

        rA.IsError.Should().BeFalse();
        rB.IsError.Should().BeFalse();

        // Both amounts must arrive at the provider un-truncated.
        provider.Verify(p => p.MintAsync(
            It.IsAny<string>(), AmountA, It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        provider.Verify(p => p.MintAsync(
            It.IsAny<string>(), AmountB, It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // THE C-1 invariant: two distinct large amounts → two distinct idempotency
        // keys → two broadcasts. Pre-fix: mintCalls == 1, RecordCount == 1 (bug).
        mintCalls.Should().Be(2,
            "C-1: distinct ulong amounts above int.MaxValue must not collide on the " +
            "idempotency key — both allocations must broadcast");
        idempotency.RecordCount.Should().Be(2,
            "two distinct large amounts must produce two independent idempotency records");
        idempotency.Keys.Distinct().Should().HaveCount(2,
            "the key is derived from the TRUE ulong Amount — no int-saturation collision");
    }
}
