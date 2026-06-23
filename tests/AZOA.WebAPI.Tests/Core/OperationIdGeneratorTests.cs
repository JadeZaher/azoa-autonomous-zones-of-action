using FluentAssertions;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Tests.Core;

/// <summary>
/// Correctness tests for <see cref="OperationIdGenerator"/> — the deterministic
/// content-addressed idempotency-key generator. The two properties that protect
/// real value are:
///   • <b>Determinism</b>: identical logical inputs ⇒ identical id (a retried
///     value-moving request collapses to one operation), and
///   • <b>Separator-safety</b>: distinct logical inputs ⇒ distinct id (a genuinely
///     different op is never silently suppressed by an aliased key, which would
///     mean stuck funds / a missing mint).
/// </summary>
public class OperationIdGeneratorTests
{
    private const string Chain = "solana";
    private const string Op = "bridge_lock";
    private const string Wallet = "WALLETADDR123";

    // ── Determinism: no time / GUID component ──────────────────────────────

    [Fact]
    public void SameInputs_ProduceSameId_NoTimeOrGuidComponent()
    {
        // Same logical op ⇒ stable id across calls (and processes).
        var a = OperationIdGenerator.Generate(Chain, Op, Wallet);
        var b = OperationIdGenerator.Generate(Chain, Op, Wallet);
        a.Should().Be(b);

        var c = OperationIdGenerator.Generate(Chain, Op, Wallet, "tokenX", "100");
        var d = OperationIdGenerator.Generate(Chain, Op, Wallet, "tokenX", "100");
        c.Should().Be(d);
    }

    [Fact]
    public void SameLogicalOp_IsStable_AcrossManyCalls()
    {
        var ids = Enumerable.Range(0, 50)
            .Select(_ => OperationIdGenerator.Generate(Chain, Op, Wallet, "p1", 42))
            .Distinct()
            .ToList();
        ids.Should().ContainSingle("a fixed logical op must map to exactly one stable id");
    }

    // ── Separator-safety: the previously-colliding cases ───────────────────

    [Fact]
    public void PreviouslyColliding_AmbiguousJoin_NowProducesDistinctIds()
    {
        // Before escaping, the params overload joined raw values with '|', so
        // these three distinct logical operations all hashed the SAME string
        // ("...|a|b|c") and collapsed to ONE idempotency key — silently
        // suppressing two genuinely different value-moving ops.
        var twoFirstParamHasPipe = OperationIdGenerator.Generate(Chain, Op, Wallet, "a|b", "c");
        var twoSecondParamHasPipe = OperationIdGenerator.Generate(Chain, Op, Wallet, "a", "b|c");
        var threeParams = OperationIdGenerator.Generate(Chain, Op, Wallet, "a", "b", "c");

        twoFirstParamHasPipe.Should().NotBe(twoSecondParamHasPipe);
        twoFirstParamHasPipe.Should().NotBe(threeParams);
        twoSecondParamHasPipe.Should().NotBe(threeParams);
        new[] { twoFirstParamHasPipe, twoSecondParamHasPipe, threeParams }
            .Distinct().Should().HaveCount(3);
    }

    [Fact]
    public void SeparatorInWalletOrChain_DoesNotAliasOntoParams()
    {
        // A '|' inside the wallet/chain/op must not be confusable with a
        // component boundary either.
        var pipeInWallet = OperationIdGenerator.Generate(Chain, Op, "wal|let");
        var splitWallet = OperationIdGenerator.Generate(Chain, Op, "wal", "let");
        pipeInWallet.Should().NotBe(splitWallet);
    }

    // ── Distinct ops ⇒ distinct ids ────────────────────────────────────────

    [Fact]
    public void DistinctOperations_ProduceDistinctIds()
    {
        var baseId = OperationIdGenerator.Generate(Chain, Op, Wallet);

        OperationIdGenerator.Generate("algorand", Op, Wallet).Should().NotBe(baseId);
        OperationIdGenerator.Generate(Chain, "burn_wrap", Wallet).Should().NotBe(baseId);
        OperationIdGenerator.Generate(Chain, Op, "OTHERWALLET").Should().NotBe(baseId);

        var p1 = OperationIdGenerator.Generate(Chain, Op, Wallet, "tokenA", "1");
        var p2 = OperationIdGenerator.Generate(Chain, Op, Wallet, "tokenA", "2");
        var p3 = OperationIdGenerator.Generate(Chain, Op, Wallet, "tokenB", "1");
        new[] { p1, p2, p3 }.Distinct().Should().HaveCount(3);
    }

    [Fact]
    public void ChainAndOpType_AreCaseInsensitive_ForDedup()
    {
        // Same logical op submitted with different chain/op casing must dedup.
        OperationIdGenerator.Generate("SOLANA", "BRIDGE_LOCK", Wallet)
            .Should().Be(OperationIdGenerator.Generate("solana", "bridge_lock", Wallet));
    }

    // ── Shape & size invariants ────────────────────────────────────────────

    [Fact]
    public void Id_KeepsExpectedShape_AndStaysWithinIdempotencyKeyBound()
    {
        // Realistic worst-case-ish lengths for chain/op (the only variable-length
        // prefix parts) plus the full 64-hex digest must stay ≤ 200 — the
        // IdempotencyRecord.Key max length.
        var longish = OperationIdGenerator.Generate(
            "some-longer-chain-name", "a_fairly_long_operation_type_name",
            new string('W', 64), new string('p', 200), 1234567890);

        longish.Should().StartWith("op_some-longer-chain-name_a_fairly_long_operation_type_name_");
        longish.Length.Should().BeLessThanOrEqualTo(200,
            "the generated key must fit IdempotencyRecord.Key (MaxLength 200)");

        // Digest segment is the full 256-bit SHA-256 (64 lowercase hex chars).
        var simple = OperationIdGenerator.Generate(Chain, Op, Wallet);
        var hashSeg = simple.Split('_').Last();
        hashSeg.Should().HaveLength(64);
        hashSeg.Should().MatchRegex("^[0-9a-f]{64}$");
        simple.Length.Should().BeLessThanOrEqualTo(200);
    }
}
