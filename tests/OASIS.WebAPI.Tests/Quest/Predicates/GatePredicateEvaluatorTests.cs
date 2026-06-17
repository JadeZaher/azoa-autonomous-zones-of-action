using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using OASIS.WebAPI.Services.Quest.Predicates;
using Xunit;

namespace OASIS.WebAPI.Tests.Quest.Predicates;

/// <summary>
/// Unit tests for the whitelisted GateCheck predicate evaluator. These exercise
/// the closed grammar (literals, dotted paths, comparison, boolean ops, parens),
/// the fail-closed semantics on missing paths, the string-ordering rejection,
/// and — critically (T11) — that arbitrary-code / out-of-grammar payloads are
/// rejected at parse with a <see cref="GatePredicateException"/> and never
/// executed.
/// </summary>
public class GatePredicateEvaluatorTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static Dictionary<string, JsonElement> Scope(params (string key, string json)[] entries)
    {
        var d = new Dictionary<string, JsonElement>();
        foreach (var (key, json) in entries) d[key] = Json(json);
        return d;
    }

    // ─── numeric comparison ───

    [Fact]
    public void NumericGreaterThan_Pass()
    {
        var scope = Scope(("upstream.bal", "{\"amount\":150}"));
        GatePredicateEvaluator.Evaluate("upstream.bal.amount > 100", scope).Should().BeTrue();
    }

    [Fact]
    public void NumericGreaterThan_Fail()
    {
        var scope = Scope(("upstream.bal", "{\"amount\":50}"));
        GatePredicateEvaluator.Evaluate("upstream.bal.amount > 100", scope).Should().BeFalse();
    }

    [Theory]
    [InlineData("upstream.bal.amount == 100", true)]
    [InlineData("upstream.bal.amount != 100", false)]
    [InlineData("upstream.bal.amount >= 100", true)]
    [InlineData("upstream.bal.amount <= 100", true)]
    [InlineData("upstream.bal.amount < 100", false)]
    [InlineData("upstream.bal.amount < 101", true)]
    public void NumericComparators(string predicate, bool expected)
    {
        var scope = Scope(("upstream.bal", "{\"amount\":100}"));
        GatePredicateEvaluator.Evaluate(predicate, scope).Should().Be(expected);
    }

    [Fact]
    public void NegativeAndDecimalNumbers()
    {
        var scope = Scope(("upstream.t", "{\"v\":-3.5}"));
        GatePredicateEvaluator.Evaluate("upstream.t.v < -3", scope).Should().BeTrue();
        GatePredicateEvaluator.Evaluate("upstream.t.v == -3.5", scope).Should().BeTrue();
    }

    // ─── string equality ───

    [Fact]
    public void StringEquality_SingleQuoted()
    {
        var scope = Scope(("upstream.kyc", "{\"status\":\"verified\"}"));
        GatePredicateEvaluator.Evaluate("upstream.kyc.status == 'verified'", scope).Should().BeTrue();
        GatePredicateEvaluator.Evaluate("upstream.kyc.status != 'pending'", scope).Should().BeTrue();
    }

    [Fact]
    public void StringEquality_DoubleQuoted()
    {
        var scope = Scope(("upstream.kyc", "{\"status\":\"verified\"}"));
        GatePredicateEvaluator.Evaluate("upstream.kyc.status == \"verified\"", scope).Should().BeTrue();
    }

    [Fact]
    public void StringOrdering_IsRejected()
    {
        var scope = Scope(("upstream.kyc", "{\"status\":\"verified\"}"));
        var act = () => GatePredicateEvaluator.Evaluate("upstream.kyc.status > 'a'", scope);
        act.Should().Throw<GatePredicateException>().WithMessage("*ordering*");
    }

    // ─── bool ───

    [Fact]
    public void BoolEquality_AndDirectOperand()
    {
        var scope = Scope(("upstream.f", "{\"ok\":true}"));
        GatePredicateEvaluator.Evaluate("upstream.f.ok == true", scope).Should().BeTrue();
        GatePredicateEvaluator.Evaluate("upstream.f.ok", scope).Should().BeTrue();
        GatePredicateEvaluator.Evaluate("!upstream.f.ok", scope).Should().BeFalse();
    }

    // ─── null ───

    [Fact]
    public void NullEquality()
    {
        var scope = Scope(("upstream.x", "{\"v\":null}"));
        GatePredicateEvaluator.Evaluate("upstream.x.v == null", scope).Should().BeTrue();
    }

    [Fact]
    public void CrossTypeEquality_IsFalse_NotThrow()
    {
        var scope = Scope(("upstream.x", "{\"v\":5}"));
        GatePredicateEvaluator.Evaluate("upstream.x.v == 'five'", scope).Should().BeFalse();
        GatePredicateEvaluator.Evaluate("upstream.x.v != 'five'", scope).Should().BeTrue();
    }

    // ─── boolean operators, parens, precedence ───

    [Fact]
    public void AndOrNot_WithParens()
    {
        var scope = Scope(("upstream.bal", "{\"amount\":150}"), ("upstream.kyc", "{\"status\":\"verified\"}"));
        GatePredicateEvaluator
            .Evaluate("upstream.bal.amount > 100 && upstream.kyc.status == 'verified'", scope)
            .Should().BeTrue();
        GatePredicateEvaluator
            .Evaluate("upstream.bal.amount > 1000 || upstream.kyc.status == 'verified'", scope)
            .Should().BeTrue();
        GatePredicateEvaluator
            .Evaluate("!(upstream.bal.amount > 1000) && upstream.kyc.status == 'verified'", scope)
            .Should().BeTrue();
    }

    [Fact]
    public void Precedence_AndBindsTighterThanOr()
    {
        // false || (true && false)  =>  false ; if || bound tighter it'd be (false||true)&&false = false too,
        // so use a case that distinguishes: true || false && false  ==  true || (false && false) == true
        var scope = Scope(("upstream.f", "{\"t\":true,\"f\":false}"));
        GatePredicateEvaluator
            .Evaluate("upstream.f.t || upstream.f.f && upstream.f.f", scope)
            .Should().BeTrue();
        // With parens forcing the other grouping it becomes false.
        GatePredicateEvaluator
            .Evaluate("(upstream.f.t || upstream.f.f) && upstream.f.f", scope)
            .Should().BeFalse();
    }

    [Fact]
    public void NotBindsTighterThanAnd()
    {
        var scope = Scope(("upstream.f", "{\"t\":true,\"f\":false}"));
        // !false && true  =>  (!false) && true  =>  true
        GatePredicateEvaluator.Evaluate("!upstream.f.f && upstream.f.t", scope).Should().BeTrue();
    }

    // ─── reads.<name> resolution ───

    [Fact]
    public void Reads_TopLevelScalar()
    {
        var scope = Scope(("reads.kyc", "\"verified\""));
        GatePredicateEvaluator.Evaluate("reads.kyc == 'verified'", scope).Should().BeTrue();
    }

    [Fact]
    public void Reads_NestedPath()
    {
        var scope = Scope(("reads.profile", "{\"tier\":3}"));
        GatePredicateEvaluator.Evaluate("reads.profile.tier >= 2", scope).Should().BeTrue();
    }

    // ─── fail-closed: missing path ───

    [Fact]
    public void MissingScopeKey_Throws()
    {
        var scope = Scope(("upstream.bal", "{\"amount\":1}"));
        var act = () => GatePredicateEvaluator.Evaluate("upstream.other.x > 1", scope);
        act.Should().Throw<GatePredicateException>().WithMessage("*no scope value bound*");
    }

    [Fact]
    public void MissingJsonMember_Throws()
    {
        var scope = Scope(("upstream.bal", "{\"amount\":1}"));
        var act = () => GatePredicateEvaluator.Evaluate("upstream.bal.missing > 1", scope);
        act.Should().Throw<GatePredicateException>().WithMessage("*not found*");
    }

    [Fact]
    public void NavigateIntoNonObject_Throws()
    {
        var scope = Scope(("upstream.bal", "{\"amount\":1}"));
        var act = () => GatePredicateEvaluator.Evaluate("upstream.bal.amount.deep > 1", scope);
        act.Should().Throw<GatePredicateException>().WithMessage("*non-object*");
    }

    // ─── SECURITY (T11): arbitrary-code / out-of-grammar payloads rejected at parse ───

    [Theory]
    [InlineData("System.IO.File.ReadAllText('x')")]
    [InlineData("foo.Bar()")]
    [InlineData("a[0]")]
    [InlineData("1; DROP")]
    [InlineData("DataTable.Compute('1','')")]
    [InlineData("upstream.bal.amount + 1 > 2")]
    [InlineData("${jndi:ldap://x}")]
    [InlineData("__import__('os')")]
    [InlineData("upstream.bal.amount =:= 1")]
    [InlineData("upstream.bal.amount > ")]
    [InlineData("&& upstream.bal.amount")]
    [InlineData("()")]
    [InlineData("upstream..amount > 1")]
    [InlineData("upstream.bal.amount > 1 unexpected")]
    [InlineData("`whoami`")]
    public void ArbitraryCodeAndMalformed_RejectedAtParse(string payload)
    {
        var scope = Scope(("upstream.bal", "{\"amount\":100}"));
        var act = () => GatePredicateEvaluator.Evaluate(payload, scope);
        act.Should().Throw<GatePredicateException>(
            because: "the closed grammar must reject anything outside literals/paths/comparison/boolean/parens");
    }

    [Fact]
    public void FunctionCallSyntax_NeverInvokesAnything()
    {
        // Even if an identifier path partially resolves, '(' after it is a parse error
        // long before any evaluation — proving no call dispatch exists.
        var scope = Scope(("reads.x", "1"));
        var act = () => GatePredicateEvaluator.Evaluate("reads.x('arg')", scope);
        act.Should().Throw<GatePredicateException>();
    }

    [Fact]
    public void UnknownBareIdentifier_DoesNotResolve_FailsClosed()
    {
        var scope = Scope(("upstream.bal", "{\"amount\":1}"));
        var act = () => GatePredicateEvaluator.Evaluate("evil == 1", scope);
        act.Should().Throw<GatePredicateException>().WithMessage("*no scope value bound*");
    }
}
