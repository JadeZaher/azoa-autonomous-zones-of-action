// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyc;

namespace AZOA.WebAPI.Tests.Kyc;

/// <summary>
/// The recourse half of the AML policy: what a subject is told when the screening
/// verdict is not clear, who they can reach, and when the operator's own deadline
/// has run out. A false-positive sanctions-name match is the common case, so an
/// unexplained refusal that names no human is a product defect, not a detail.
/// </summary>
public sealed class KycAmlScreeningRecourseTests
{
    [Fact]
    public void ClearanceAndManualReview_AreExactComplements()
    {
        // Both value gates read IsClear and the tenant projection reads
        // RequiresManualReview. A subject the gate denies but the projection calls
        // ready is the stranded case; a subject the gate opens but the projection
        // calls under review is the reverse. Neither may exist.
        KycScreeningResult?[] rows =
        [
            null,
            Row(KycScreeningDecision.NO_HIT),
            Row(KycScreeningDecision.NO_HIT, resultsTruncated: true),
            Row(KycScreeningDecision.MANUAL_REVIEW),
            Row(KycScreeningDecision.HIT),
        ];

        foreach (var row in rows)
        {
            KycAmlClearance.RequiresManualReview(row)
                .Should().Be(!KycAmlClearance.IsClear(row));
        }
    }

    [Fact]
    public void AContactTheOperatorSet_IsProjectedAndNamedInTheSubjectWording()
    {
        var configuration = Configure("compliance@node.example");

        KycAmlClearance.ResolveManualReviewContact(configuration)
            .Should().Be("compliance@node.example");
        KycAmlClearance.ResolveUnderReviewReason(configuration)
            .Should().StartWith(KycAmlClearance.UnderReviewReason)
            .And.Contain("compliance@node.example");
    }

    [Fact]
    public void ASurroundingWhitespaceContact_IsTrimmedNotRejected()
    {
        KycAmlClearance.ResolveManualReviewContact(Configure("  https://node.example/help  "))
            .Should().Be("https://node.example/help");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentContact_ResolvesToNullAndLeavesTheWordingUnchanged(string? configured)
    {
        var configuration = Configure(configured);

        KycAmlClearance.ResolveManualReviewContact(configuration).Should().BeNull();
        KycAmlClearance.ResolveUnderReviewReason(configuration)
            .Should().Be(KycAmlClearance.UnderReviewReason);
    }

    [Fact]
    public void AContactCarryingControlCharacters_IsRefusedRatherThanProjected()
    {
        // It would travel into a tenant response, an operator log and, in a tenant
        // that renders it as a link, a header. A route a subject cannot use also
        // reads as recourse that exists, which is worse than none at all.
        KycAmlClearance.ResolveManualReviewContact(
                Configure("support@node.example\r\nX-Injected: header"))
            .Should().BeNull();
    }

    [Fact]
    public void AnOverLongContact_IsRefusedAtTheProjectionBoundary()
    {
        var tooLong = new string('a', KycAmlClearance.MaximumManualReviewContactLength + 1);

        KycAmlClearance.ResolveManualReviewContact(Configure(tooLong)).Should().BeNull();
        KycAmlClearance.ResolveManualReviewContact(
                Configure(new string('a', KycAmlClearance.MaximumManualReviewContactLength)))
            .Should().NotBeNull();
    }

    [Fact]
    public void TheSubjectWording_NamesNoListScoreTopicOrVendorEvenWithAContact()
    {
        var reason = KycAmlClearance.ResolveUnderReviewReason(
            Configure("compliance@node.example"));

        reason.Should().NotContainEquivalentOf("sanction");
        reason.Should().NotContainEquivalentOf("watchlist");
        reason.Should().NotContainEquivalentOf("identomat");
        reason.Should().NotContainEquivalentOf("score");
    }

    [Fact]
    public void ANullDeadline_IsNotOverdue()
    {
        // Null means nothing has been screened yet. That is a worse failure with its
        // own alarm, but calling it "overdue" would invent a deadline nobody started.
        KycAmlClearance.IsOverdue(null, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void ADeadlineStillRunning_IsNotOverdueAndOneThatPassedIs()
    {
        var now = DateTimeOffset.UtcNow;

        KycAmlClearance.IsOverdue(now.AddMinutes(1), now).Should().BeFalse();
        KycAmlClearance.IsOverdue(now, now).Should().BeFalse("the deadline is inclusive");
        KycAmlClearance.IsOverdue(now.AddMinutes(-1), now).Should().BeTrue();
    }

    [Fact]
    public void TheDeadline_RunsFromTheScreeningAndOnlyForAnOpenReview()
    {
        var screenedAt = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var sla = TimeSpan.FromHours(KycAmlClearance.DefaultManualReviewSlaHours);

        KycAmlClearance.ReviewDueAt(Row(KycScreeningDecision.MANUAL_REVIEW, screenedAt), sla)
            .Should().Be(screenedAt.Add(sla));
        KycAmlClearance.ReviewDueAt(Row(KycScreeningDecision.NO_HIT, screenedAt), sla)
            .Should().BeNull();
        KycAmlClearance.ReviewDueAt(null, sla)
            .Should().BeNull("the SLA clock starts at the screening, not at the approval");
    }

    [Theory]
    [InlineData("0", KycAmlClearance.MinimumManualReviewSlaHours)]
    [InlineData("-5", KycAmlClearance.MinimumManualReviewSlaHours)]
    [InlineData("100000", KycAmlClearance.MaximumManualReviewSlaHours)]
    [InlineData("24", 24)]
    public void TheOperatorSla_IsClampedSoATypoCannotRemoveTheDeadline(
        string configured,
        int expectedHours)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KycAmlClearance.ManualReviewSlaHoursConfigurationPath] = configured,
            })
            .Build();

        KycAmlClearance.ResolveManualReviewSla(configuration)
            .Should().Be(TimeSpan.FromHours(expectedHours));
    }

    private static IConfiguration Configure(string? contact)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KycAmlClearance.ManualReviewContactConfigurationPath] = contact,
            })
            .Build();

    private static KycScreeningResult Row(
        KycScreeningDecision decision,
        DateTimeOffset? screenedAt = null,
        bool resultsTruncated = false) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Decision = decision,
            ResultsTruncated = resultsTruncated,
            PolicyVersion = KycScreeningRiskPolicy.CurrentPolicyVersion,
            ScreenedAt = screenedAt ?? DateTimeOffset.UtcNow,
        };
}
