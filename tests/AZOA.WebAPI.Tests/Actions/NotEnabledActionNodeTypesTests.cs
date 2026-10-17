using System.Text.RegularExpressions;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// Binds the governance half of <see cref="NotEnabledActionNodeTypes"/> to the
/// INDEPENDENT review verdict that is allowed to lift it (L4.1 / OI-50).
/// </summary>
/// <remarks>
/// <para>
/// Phase 4 of <c>governance-vote-of-record_20260906</c> says a phase with no recorded
/// verdict is unreviewed rather than done. That was a sentence in a plan, and a
/// sentence in a plan is the exact thing this repo keeps mistaking for a control — so
/// here it is as one. The blocked-state of the three governance node types must agree
/// with the single <c>VERDICT:</c> line in the review file; flipping either alone goes
/// red, and the reviewer's findings and the gate can no longer drift apart.
/// </para>
/// <para>
/// Deliberately NOT a second copy of <c>NotEnabledNodeTypeGateTests</c>, which already
/// covers the blocked set, both dispatch seams and the publish refusal. This file
/// covers only the one thing that file cannot: whether the gate still matches the
/// reason it is standing on.
/// </para>
/// </remarks>
public sealed class NotEnabledActionNodeTypesTests
{
    /// <summary>The node types whose gate this particular verdict governs. CredentialRevoke has its own track.</summary>
    public static TheoryData<ActionNodeType> GovernanceNodeTypes =>
        new()
        {
            ActionNodeType.GovernanceProposalCreate,
            ActionNodeType.GovernanceVote,
            ActionNodeType.GovernanceTally,
        };

    private static readonly string[] VerdictRelativePath =
    {
        "conductor", "tracks", "platform-stub-closeout_20261015", "round2-governance-source-review.md",
    };

    private static readonly string[] RuntimeVerdictRelativePath =
    {
        "conductor", "tracks", "platform-stub-closeout_20261015", "round4-governance-runtime-verdict.md",
    };

    private static readonly Regex RuntimeVerdictMarker =
        new(@"^RUNTIME_VERDICT:[ \t]*(PENDING|ACCEPTED|REJECTED)\b", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // Line-anchored, and tolerant of this tree's mixed CRLF/LF: Multiline '^' matches
    // after either ending. The file is written so exactly ONE line starts with VERDICT:.
    private static readonly Regex VerdictMarker =
        new(@"^SOURCE_VERDICT:[ \t]*(ACCEPTED|REJECTED)\b", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    [Fact]
    public void TheIndependentVerdict_IsRecordedExactlyOnce()
    {
        var matches = VerdictMarker.Matches(VerdictText());

        matches.Count.Should().Be(
            1,
            "a review whose verdict cannot be read off unambiguously has not recorded one; "
            + "write exactly one line starting 'SOURCE_VERDICT: ACCEPTED' or 'SOURCE_VERDICT: REJECTED'");
    }

    [Fact]
    public void TheRuntimeVerdict_IsRecordedExactlyOnce()
    {
        RuntimeVerdictMarker.Matches(SourceOf(RuntimeVerdictRelativePath)).Count.Should().Be(
            1,
            "the runtime verdict file must carry exactly one 'RUNTIME_VERDICT: PENDING|ACCEPTED|REJECTED' line");
    }

    /// <summary>Plan §0.2: source acceptance alone never lifts the gate; a runtime verdict after green race fixtures does.</summary>
    [Theory]
    [MemberData(nameof(GovernanceNodeTypes))]
    public void GovernanceTypes_AreBlocked_UntilBothSourceAndRuntimeVerdictsAccept(ActionNodeType nodeType)
    {
        var runtime = RuntimeVerdictMarker.Match(SourceOf(RuntimeVerdictRelativePath)).Groups[1].Value;
        var lifted = RecordedVerdict() == "ACCEPTED" && runtime == "ACCEPTED";

        NotEnabledActionNodeTypes.IsBlocked(nodeType).Should().Be(
            !lifted,
            "the governance gate lifts only on both recorded verdicts — source says {0}, runtime says {1}, so {2} must be {3}",
            RecordedVerdict(),
            runtime,
            nodeType,
            lifted ? "enabled" : "blocked");
    }

    [Fact]
    public void TheGateSourceNamesTheVerdictFileItIsStandingOn()
    {
        var gateSource = SourceOf("Services", "Actions", "NotEnabledActionNodeTypes.cs");

        gateSource.Should().Contain(
            "round2-governance-source-review.md",
            "a gate that stops naming its verdict is a gate whose reason has gone folkloric — "
            + "the previous reason (a tally that read raw holons) was already stale when L4.1 read it")
            .And.Contain("round4-governance-runtime-verdict.md");
    }

    private static string RecordedVerdict()
    {
        var match = VerdictMarker.Match(VerdictText());
        return match.Success
            ? match.Groups[1].Value
            : throw new InvalidOperationException(
                "round2-governance-source-review.md records no 'SOURCE_VERDICT: ACCEPTED|REJECTED' line.");
    }

    private static string VerdictText() => SourceOf(VerdictRelativePath);

    /// <summary>
    /// Reads a repo file by walking up to the solution. Probes the SOLUTION FILE, not a
    /// directory name: the test project mirrors several source paths, so a directory
    /// probe resolves the repo root to tests/AZOA.WebAPI.Tests.
    /// </summary>
    private static string SourceOf(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "azoa.sln")))
                return File.ReadAllText(Path.Combine(new[] { directory.FullName }.Concat(relativePath).ToArray()));
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the AZOA repository root.");
    }
}
