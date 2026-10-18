using System.Text.RegularExpressions;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// Guards that "not enabled" is a CONTROL, not a claim.
///
/// The Phase 5 governance and credential rails were described as not enabled in the
/// runbook, the track specs and the builder's palette badge — while every handler was
/// assembly-scanned into DI, the config registry accepted their configs, and both
/// dispatch seams would have run them. The badge was frontend copy. These tests fail
/// if any of the three refusals is removed.
/// </summary>
public sealed class NotEnabledNodeTypeGateTests
{
    private static string SourceOf(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            // Probe the solution file. Probing a DIRECTORY name is unsafe here: the test
            // project mirrors several source paths, so a directory probe resolves the
            // repo root to tests/AZOA.WebAPI.Tests.
            if (File.Exists(Path.Combine(directory.FullName, "azoa.sln")))
                return File.ReadAllText(Path.Combine(new[] { directory.FullName }.Concat(relativePath).ToArray()));
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate the AZOA repository root.");
    }

    [Theory]
    [InlineData(ActionNodeType.CredentialRevoke)]
    public void KnownBrokenPrimitives_AreBlocked(ActionNodeType nodeType) =>
        NotEnabledActionNodeTypes.IsBlocked(nodeType).Should().BeTrue(
            "governance cannot be trusted to count and the credential rail cannot "
            + "authorize; both are gated until their design tracks close");

    [Theory]
    [InlineData(ActionNodeType.LoanOpen)]
    [InlineData(ActionNodeType.LoanRepaymentRecord)]
    [InlineData(ActionNodeType.LoanBalance)]
    [InlineData(ActionNodeType.HolonCreate)]
    [InlineData(ActionNodeType.Transfer)]
    public void EverythingElse_IsNotBlocked(ActionNodeType nodeType) =>
        NotEnabledActionNodeTypes.IsBlocked(nodeType).Should().BeFalse(
            "the gate must not quietly widen — it is a named, tracked exception list");

    /// <summary>
    /// The refusal must be enforced at BOTH dispatch seams, not merely defined. This is
    /// the exact failure mode the gate exists to correct, so it is asserted structurally
    /// against the two seams' source rather than trusted.
    /// </summary>
    [Theory]
    [InlineData("Managers", "ActionManager.cs")]
    [InlineData("Services", "Actions", "Workflow", "ActionNodeStepHandler.cs")]
    public void BothDispatchSeams_ConsultTheGate(params string[] relativePath)
    {
        var source = SourceOf(relativePath);

        source.Should().Contain("NotEnabledActionNodeTypes.IsBlocked",
            "a dispatch seam that does not consult the gate is a way around it — the "
            + "durable and in-process paths must refuse identically");
    }

    [Fact]
    public void Publish_RefusesABlockedNodeType()
    {
        var source = SourceOf("Managers", "ActionManager.cs");

        var publish = source[source.IndexOf("public async Task<AZOAResult<ActionDefinition>> PublishAsync", StringComparison.Ordinal)..];
        var body = publish[..publish.IndexOf("\n    public ", StringComparison.Ordinal)];

        body.Should().Contain("NotEnabledActionNodeTypes.IsBlocked",
            "a blocked node type must not be able to enter an Active action");
    }

    /// <summary>The catalog surface must not advertise a blocked type as runnable.</summary>
    [Fact]
    public void EveryBlockedType_IsANamedTrackedException()
    {
        NotEnabledActionNodeTypes.All.Should().BeEquivalentTo(new[]
            {
                ActionNodeType.CredentialRevoke,
                ActionNodeType.GovernanceProposalCreate,
                ActionNodeType.GovernanceVote,
                ActionNodeType.GovernanceTally,
            },
            "each blocked type is tracked by a design track or verdict; adding one without a track "
            + "turns a temporary gate into silent dead surface; lifting one must change this test with its verdict (RV-L1-05)");

        var source = SourceOf("Services", "Actions", "NotEnabledActionNodeTypes.cs");
        source.Should().Contain("round2-governance-source-review.md",
            "source activation must name its independently recorded review");
    }

    /// <summary>CredentialRevoke lifts only as the final Phase 4 step of its own track, after its recorded security review.</summary>
    [Fact]
    public void CredentialRevoke_StaysBlockedUntilItsTrackChecksTheGateRemovalStep()
    {
        var plan = SourceOf("conductor", "tracks", "credential-third-party-issuance_20260906", "plan.md");
        var step = System.Text.RegularExpressions.Regex.Match(plan,
            @"^- \[(?<done>[ x])\] Remove `Services/Actions/NotEnabledActionNodeTypes`' `CredentialRevoke` entry",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        step.Success.Should().BeTrue("the track must keep its explicit gate-removal step");
        NotEnabledActionNodeTypes.IsBlocked(ActionNodeType.CredentialRevoke).Should().Be(step.Groups["done"].Value != "x",
            "the gate and its track's recorded Phase 4 step must change together");
    }
}
