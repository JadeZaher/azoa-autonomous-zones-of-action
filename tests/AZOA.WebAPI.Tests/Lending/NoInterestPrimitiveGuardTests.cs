using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Services.Lending;
using Xunit;

namespace AZOA.WebAPI.Tests.Lending;

/// <summary>
/// D21's absence-is-the-feature guard. The principal-only loan tracker has no interest,
/// and no accrual node may ever exist: reversing that "requires scholar review, not just
/// code" (D21 §4 assumption 5). A comment saying so stops nobody — this test reflects over
/// the whole lane (its types, members, parameters, persisted metadata vocabulary, node
/// output keys, and the <see cref="ActionNodeType"/> members it owns) and fails the build
/// the moment a time-value concept appears under any name.
/// </summary>
/// <remarks>
/// A future agent asked to "just add an optional rate field" will trip this. That is the
/// point: adding one means deleting a test that says, in words, why it exists — a decision
/// nobody can make by accident. See <c>Services/Actions/AGENTS.md</c> §loan-tracker.
/// </remarks>
public class NoInterestPrimitiveGuardTests
{
    /// <summary>
    /// Time-value vocabulary forbidden anywhere in this lane. Matched as case-insensitive
    /// substrings, which is why the scan is scoped to the lane's own surface: the wider
    /// enum contains innocent members such as <c>StarGenerate</c> that contain "rate".
    /// </summary>
    private static readonly string[] ForbiddenInLane =
    {
        "interest", "apr", "rate", "accru", "compound", "amortiz",
        "term", "tenor", "duedate", "penalty", "usury", "coupon", "yield",
    };

    /// <summary>
    /// The unambiguous subset, safe to run against EVERY node type: no innocent identifier
    /// contains these, so they can police the whole enum without false positives.
    /// </summary>
    private static readonly string[] ForbiddenEverywhere =
    {
        "interest", "accru", "usury", "compound", "amortiz",
    };

    /// <summary>The lane's own types: everything in the Lending namespace plus the three node configs and handlers.</summary>
    private static IEnumerable<Type> LaneTypes() =>
        typeof(LoanHolonTypes).Assembly
            .GetTypes()
            .Where(t => t.Namespace == "AZOA.WebAPI.Services.Lending")
            .Concat(new[]
            {
                typeof(LoanOpenNodeConfig),
                typeof(LoanRepaymentRecordNodeConfig),
                typeof(LoanBalanceNodeConfig),
                typeof(LoanOpenNodeHandler),
                typeof(LoanRepaymentRecordNodeHandler),
                typeof(LoanBalanceNodeHandler),
            })
            // Compiler-generated closures/state machines carry no author intent.
            .Where(t => !t.Name.Contains('<'));

    [Fact]
    public void NoLaneTypeOrMemberNamesATimeValueConcept()
    {
        var offenders = new List<string>();

        foreach (var type in LaneTypes())
        {
            Check(type.Name, $"type {type.FullName}", offenders);

            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var member in type.GetMembers(flags).Where(m => !m.Name.Contains('<')))
            {
                Check(member.Name, $"{type.Name}.{member.Name}", offenders);

                if (member is MethodBase method)
                    foreach (var parameter in method.GetParameters())
                        Check(parameter.Name, $"{type.Name}.{member.Name}({parameter.Name})", offenders);
            }
        }

        offenders.Should().BeEmpty(
            "the principal-only loan tracker (D21) has no interest, accrual, term or penalty concept, "
            + "and adding one is a scholar-review decision, not a code change");
    }

    [Fact]
    public void NoPersistedMetadataKeyNamesATimeValueConcept()
    {
        var offenders = new List<string>();

        foreach (var field in typeof(LoanHolonTypes.MetadataKeys)
                     .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
        {
            Check((string?)field.GetRawConstantValue(), $"metadata key '{field.Name}'", offenders);
        }

        foreach (var seed in LoanHolonTypes.RegistrySeeds())
        {
            Check(seed.AssetType, $"registry seed '{seed.AssetType}'", offenders);
            foreach (var required in seed.RequiredMetadataFields ?? Array.Empty<string>())
                Check(required, $"registry seed '{seed.AssetType}' required field '{required}'", offenders);
        }

        offenders.Should().BeEmpty("a loan holon may not persist a time-value field under any name");
    }

    [Fact]
    public void NoLoanNodeTypeNamesATimeValueConcept()
    {
        var offenders = new List<string>();

        foreach (var name in Enum.GetNames<ActionNodeType>().Where(n => n.StartsWith("Loan", StringComparison.Ordinal)))
            Check(name, $"ActionNodeType.{name}", offenders);

        offenders.Should().BeEmpty("no LoanInterestAccrual-shaped node may join the enum");
    }

    [Fact]
    public void NoNodeTypeAnywhereIsAnAccrualPrimitive()
    {
        var offenders = Enum.GetNames<ActionNodeType>()
            .Where(name => ForbiddenEverywhere.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        offenders.Should().BeEmpty(
            "D21: 'no interest-accrual node may ever exist — the absence is the feature'");
    }

    [Fact]
    public void ProjectionOutputCarriesExactlyTheSevenDocumentedKeys()
    {
        var totals = LoanBalanceProjection.ToResult(Guid.NewGuid(), new LoanBalanceTotals(
            RailAmount.OfChainAsset(1000, "ALGO"),
            RailAmount.OfChainAsset(400, "ALGO"),
            RailAmount.OfChainAsset(0, "ALGO"),
            RailAmount.OfChainAsset(600, "ALGO"),
            LoanStatus.Open,
            2));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(totals));
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        keys.Should().BeEquivalentTo(new[]
        {
            "loanId", "principal", "totalRepaid", "totalWrittenOff", "outstanding", "status", "entryCount",
        }, "the projection's published shape is fixed — a new key is a new concept");

        var offenders = new List<string>();
        foreach (var key in keys)
            Check(key, $"output key '{key}'", offenders);
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void EveryProjectedAmountIsAString_NeverAJsonNumber()
    {
        var totals = LoanBalanceProjection.ToResult(Guid.NewGuid(), new LoanBalanceTotals(
            RailAmount.OfChainAsset(BigInteger.Parse("100000000000000000000"), "ETH"),
            RailAmount.OfChainAsset(0, "ETH"),
            RailAmount.OfChainAsset(0, "ETH"),
            RailAmount.OfChainAsset(BigInteger.Parse("100000000000000000000"), "ETH"),
            LoanStatus.Open,
            1));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(totals));

        foreach (var name in new[] { "principal", "totalRepaid", "totalWrittenOff", "outstanding" })
            document.RootElement.GetProperty(name).ValueKind
                .Should().Be(JsonValueKind.String, $"{name} is a base-unit decimal string (D13), not a JSON number");
    }

    private static void Check(string? candidate, string where, List<string> offenders)
    {
        if (string.IsNullOrEmpty(candidate))
            return;

        foreach (var token in ForbiddenInLane)
            if (candidate.Contains(token, StringComparison.OrdinalIgnoreCase))
                offenders.Add($"{where} contains forbidden time-value token '{token}'");
    }
}
