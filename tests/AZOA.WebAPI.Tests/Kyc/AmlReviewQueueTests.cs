// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Admin;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Kyc;

/// <summary>
/// The AML manual-review desk: the queue a flagged person waits in, and the decision
/// that ends the wait. Failing closed with no door is indistinguishable from a ban,
/// so the queue must show the people who are stranded and the decision must actually
/// move the verdict the value gates read.
/// </summary>
public sealed class AmlReviewQueueTests
{
    private const string ProviderKey = "veriff";
    /// <summary>The node operator's fixed subject id; the desk refuses anyone else.</summary>
    private static readonly Guid OperatorId = NodeOperatorIdentity.UserId;
    private const string Reason =
        "Date of birth and nationality both differ from the listed person.";

    [Fact]
    public async Task Review_of_an_obsolete_attempt_cannot_clear_the_current_approval()
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        screening.SeedResult(userId, null, KycScreeningDecision.HIT,
            DateTimeOffset.UtcNow.AddHours(-1), lineageKey: "review-lineage");
        var obsolete = screening.SeedResult(userId, null, KycScreeningDecision.MANUAL_REVIEW,
            lineageKey: "obsolete-attempt");
        var result = await NewManager(ApprovedStore(userId).Object, screening)
            .DecideAmlReviewAsync(userId, ClearRequest(obsolete.Id), OperatorId);
        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.Conflict);
        screening.SavedResults.Should().HaveCount(2);
    }

    // -- The queue ----------------------------------------------------------

    [Fact]
    public async Task Queue_ListsAVerifiedPersonWhoseScreeningIsNotClear()
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        var flagged = screening.SeedResult(
            userId, null, KycScreeningDecision.MANUAL_REVIEW,
            screenedAt: DateTimeOffset.UtcNow.AddHours(-4), lineageKey: "review-lineage");
        var manager = NewManager(ApprovedStore(userId).Object, screening);

        var page = await manager.ListAmlReviewsAsync(25, null, OperatorId);

        page.IsError.Should().BeFalse(page.Message);
        var item = page.Result!.Items.Should().ContainSingle().Subject;
        item.UserId.Should().Be(userId);
        item.TenantId.Should().BeNull();
        item.Decision.Should().Be(nameof(KycScreeningDecision.MANUAL_REVIEW));
        item.ScreeningId.Should().Be(flagged.Id);
        item.DecisionAllowed.Should().BeTrue();
        item.ReviewDueAt.Should().Be(
            flagged.ScreenedAt.Add(KycAmlClearance.ResolveManualReviewSla(null)),
            "the SLA clock starts at the screen, not at the operator's next login");
    }

    [Fact]
    public async Task Queue_OmitsAVerifiedPersonWhoseScreeningIsClear()
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        screening.SeedResult(userId, null, KycScreeningDecision.NO_HIT, lineageKey: "review-lineage");

        var page = await NewManager(ApprovedStore(userId).Object, screening)
            .ListAmlReviewsAsync(25, null, OperatorId);

        page.IsError.Should().BeFalse(page.Message);
        page.Result!.Items.Should().BeEmpty("a cleared person is waiting on nobody");
    }

    [Fact]
    public async Task Queue_ShowsAVerifiedPersonWhoWasNeverScreened_AndOffersNoDecision()
    {
        // Verified with no AML answer is a screening service that never ran, not a
        // clean subject. It belongs on the desk as an alarm, but nothing here can be
        // decided: a hand-written NO_HIT would be a clean verdict nobody produced.
        var userId = Guid.NewGuid();

        var page = await NewManager(ApprovedStore(userId).Object, new InMemoryKycScreeningStore())
            .ListAmlReviewsAsync(25, null, OperatorId);

        page.IsError.Should().BeFalse(page.Message);
        var item = page.Result!.Items.Should().ContainSingle().Subject;
        item.Decision.Should().Be(AmlManualReviewItem.NotScreened);
        item.ScreeningId.Should().BeNull();
        item.DecisionAllowed.Should().BeFalse();
        item.ReviewDueAt.Should().BeNull("no screen means no SLA clock has started");
    }

    [Fact]
    public async Task Queue_OmitsAPersonWhoseIdentityApprovalExpired()
    {
        // They are closed for a different reason and an AML decision would not open
        // them, so listing them would send an operator to a door that is not the one.
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        screening.SeedResult(userId, null, KycScreeningDecision.MANUAL_REVIEW, lineageKey: "review-lineage");
        var store = ApprovedStore(userId, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var page = await NewManager(store.Object, screening).ListAmlReviewsAsync(25, null, OperatorId);

        page.IsError.Should().BeFalse(page.Message);
        page.Result!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Queue_WhenTheScreeningLedgerCannotBeRead_FailsRatherThanLookingEmpty()
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore { FailReads = true };
        screening.SeedResult(userId, null, KycScreeningDecision.MANUAL_REVIEW, lineageKey: "review-lineage");

        var page = await NewManager(ApprovedStore(userId).Object, screening)
            .ListAmlReviewsAsync(25, null, OperatorId);

        page.IsError.Should().BeTrue(
            "\"no one is waiting\" and \"nobody is listening\" are opposite facts");
    }

    [Fact]
    public async Task Queue_WithNoScreeningLedgerWired_RefusesRatherThanLookingEmpty()
    {
        var page = await NewManager(ApprovedStore(Guid.NewGuid()).Object, screening: null)
            .ListAmlReviewsAsync(25, null, OperatorId);

        page.IsError.Should().BeTrue();
        page.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
    }

    [Fact]
    public async Task Queue_CarriesTheOperatorContactSoTheConsoleCanSeeWhoTheSubjectIsTold()
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        screening.SeedResult(userId, null, KycScreeningDecision.HIT, lineageKey: "review-lineage");
        var configuration = Configure("compliance@node.example");

        var page = await NewManager(ApprovedStore(userId).Object, screening, configuration)
            .ListAmlReviewsAsync(25, null, OperatorId);

        var item = page.Result!.Items.Should().ContainSingle().Subject;
        item.ReviewContact.Should().Be("compliance@node.example");
        item.SubjectNotice.Should().Be(
            KycAmlClearance.ResolveUnderReviewReason(configuration),
            "the console must render the node's own wording, not a second copy of it");
    }

    // -- The decision -------------------------------------------------------

    [Fact]
    public async Task ClearingAReview_WritesASupersedingRowAndTheValueGateReOpens()
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        var flagged = screening.SeedResult(
            userId, null, KycScreeningDecision.MANUAL_REVIEW,
            screenedAt: DateTimeOffset.UtcNow.AddHours(-4), lineageKey: "review-lineage");
        var store = ApprovedStore(userId);

        (await NewGate(store.Object, screening).RequireVerifiedAsync(userId))
            .IsError.Should().BeTrue("the flagged verdict is what holds this person");

        var decided = await NewManager(store.Object, screening).DecideAmlReviewAsync(
            userId, ClearRequest(flagged.Id), OperatorId);

        decided.IsError.Should().BeFalse(decided.Message);
        decided.Result!.Decision.Should().Be(nameof(KycScreeningDecision.NO_HIT));
        decided.Result.DecisionAllowed.Should().BeFalse("the review is now closed");

        screening.SavedResults.Should().HaveCount(2, "a decision supersedes, it does not edit");
        flagged.Decision.Should().Be(
            KycScreeningDecision.MANUAL_REVIEW,
            "the evidence that was decided over stays readable beside the decision");
        var superseding = screening.SavedResults.Single(row => row.Id != flagged.Id);
        superseding.Decision.Should().Be(KycScreeningDecision.NO_HIT);
        superseding.PolicyVersion.Should().Be(KycControlPlaneManager.ManualReviewPolicyVersion,
            "a machine verdict and a human one must stay tellable apart");
        superseding.ScreenedAt.Should().BeAfter(flagged.ScreenedAt,
            "a row that does not sort last supersedes nothing");

        (await NewGate(store.Object, screening).RequireVerifiedAsync(userId))
            .IsError.Should().BeFalse("the whole point of the desk is that the gate re-opens");
    }

    [Fact]
    public async Task ClearingAReview_DoesNotCopyTheRetainedNameAndDateOfBirthForward()
    {
        // D25: the query shape is retained PII. A decision is not a new search, so
        // duplicating it would leave a second copy for the erasure sweep to find.
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        var flagged = screening.SeedResult(userId, null, KycScreeningDecision.MANUAL_REVIEW,
            screenedAt: DateTimeOffset.UtcNow.AddHours(-1), lineageKey: "review-lineage");
        flagged.QueryFullName.Should().NotBeNull("the seeded row carries the query shape");
        flagged.TopScore = 74;
        flagged.HitCount = 3;
        flagged.Topics = ["sanction"];
        flagged.ScoreThreshold = 65;
        flagged.RetentionExpiresAt = DateTimeOffset.UtcNow.AddDays(365);

        await NewManager(ApprovedStore(userId).Object, screening)
            .DecideAmlReviewAsync(userId, ClearRequest(flagged.Id), OperatorId);

        var superseding = screening.SavedResults.Single(row => row.Id != flagged.Id);
        superseding.QueryFullName.Should().BeNull();
        superseding.QueryBirthdayTimeFrom.Should().BeNull();
        superseding.TopScore.Should().Be(74, "the evidence decided over carries forward");
        superseding.HitCount.Should().Be(3);
        superseding.Topics.Should().BeEquivalentTo(new[] { "sanction" });
        superseding.ScoreThreshold.Should().Be(65);
        superseding.RetentionExpiresAt.Should().Be(
            flagged.RetentionExpiresAt,
            "the decision row must not outlive the retention window it inherited");
    }

    [Fact]
    public async Task DenyingAReview_RecordsAnAdverseVerdictAndTheGateStaysShut()
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        var flagged = screening.SeedResult(userId, null, KycScreeningDecision.MANUAL_REVIEW,
            screenedAt: DateTimeOffset.UtcNow.AddHours(-1), lineageKey: "review-lineage");
        var store = ApprovedStore(userId);

        var decided = await NewManager(store.Object, screening).DecideAmlReviewAsync(
            userId,
            new AmlManualReviewDecisionRequest
            {
                Decision = AmlManualReviewDecisionRequest.Deny,
                ExpectedScreeningId = flagged.Id,
                Reason = "Identity, date of birth and nationality all match the listing.",
            },
            OperatorId);

        decided.IsError.Should().BeFalse(decided.Message);
        screening.SavedResults.Single(row => row.Id != flagged.Id)
            .Decision.Should().Be(KycScreeningDecision.HIT);
        (await NewGate(store.Object, screening).RequireVerifiedAsync(userId))
            .IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ADecisionOverAScreeningThatIsNoLongerTheLatest_Conflicts()
    {
        // The operator decides over evidence they read. If monitoring wrote a fresh
        // HIT while the dialog was open, clearing would answer the wrong question.
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        var read = screening.SeedResult(userId, null, KycScreeningDecision.MANUAL_REVIEW,
            screenedAt: DateTimeOffset.UtcNow.AddHours(-4), lineageKey: "review-lineage");
        screening.SeedResult(userId, null, KycScreeningDecision.HIT,
            screenedAt: DateTimeOffset.UtcNow.AddMinutes(-1), lineageKey: "review-lineage");

        var decided = await NewManager(ApprovedStore(userId).Object, screening)
            .DecideAmlReviewAsync(userId, ClearRequest(read.Id), OperatorId);

        decided.IsError.Should().BeTrue();
        decided.Code.Should().Be(AzoaErrorCodes.Conflict);
        screening.SavedResults.Should().HaveCount(2, "nothing was written");
    }

    [Fact]
    public async Task ClearingSomeoneWhoWasNeverScreened_IsRefused()
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();

        var decided = await NewManager(ApprovedStore(userId).Object, screening)
            .DecideAmlReviewAsync(
                userId, ClearRequest(Guid.NewGuid().ToString("N")), OperatorId);

        decided.IsError.Should().BeTrue();
        decided.Code.Should().Be(AzoaErrorCodes.OperationNotAllowed);
        screening.SavedResults.Should().BeEmpty(
            "an invented NO_HIT is a clean verdict nobody produced");
    }

    [Fact]
    public async Task ClearingSomeoneWhoIsAlreadyClear_IsRefused()
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        var clear = screening.SeedResult(userId, null, KycScreeningDecision.NO_HIT, lineageKey: "review-lineage");

        var decided = await NewManager(ApprovedStore(userId).Object, screening)
            .DecideAmlReviewAsync(userId, ClearRequest(clear.Id), OperatorId);

        decided.IsError.Should().BeTrue();
        decided.Code.Should().Be(AzoaErrorCodes.OperationNotAllowed);
        screening.SavedResults.Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ADecisionWithoutAReason_IsRefused(string reason)
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        var flagged = screening.SeedResult(userId, null, KycScreeningDecision.MANUAL_REVIEW, lineageKey: "review-lineage");

        var decided = await NewManager(ApprovedStore(userId).Object, screening)
            .DecideAmlReviewAsync(
                userId,
                new AmlManualReviewDecisionRequest
                {
                    Decision = AmlManualReviewDecisionRequest.Clear,
                    ExpectedScreeningId = flagged.Id,
                    Reason = reason,
                },
                OperatorId);

        decided.IsError.Should().BeTrue();
        decided.Code.Should().Be(AzoaErrorCodes.InvalidRequest);
        screening.SavedResults.Should().ContainSingle("nothing was written");
    }

    [Fact]
    public async Task ADecisionInOneTenantDoesNotClearTheSamePersonOnTheNode()
    {
        // The AML surface keys on a person's name and date of birth, which are shared
        // across tenants by nature. A tenant-blind write would open a gate nobody
        // decided about.
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        var tenantRow = screening.SeedResult(userId, tenantId, KycScreeningDecision.MANUAL_REVIEW,
            screenedAt: DateTimeOffset.UtcNow.AddHours(-2), lineageKey: "review-lineage");
        screening.SeedResult(userId, null, KycScreeningDecision.MANUAL_REVIEW,
            screenedAt: DateTimeOffset.UtcNow.AddHours(-2), lineageKey: "review-lineage");

        var decided = await NewManager(ApprovedStore(userId, tenantId).Object, screening)
            .DecideAmlReviewAsync(
                userId,
                new AmlManualReviewDecisionRequest
                {
                    Decision = AmlManualReviewDecisionRequest.Clear,
                    TenantId = tenantId,
                    ExpectedScreeningId = tenantRow.Id,
                    Reason = Reason,
                },
                OperatorId);

        decided.IsError.Should().BeFalse(decided.Message);
        decided.Result!.TenantId.Should().Be(tenantId);

        var tenantLatest = await screening.GetLatestResultAsync(userId, tenantId);
        KycAmlClearance.IsClear(tenantLatest.Result).Should().BeTrue();
        var nodeLatest = await screening.GetLatestResultAsync(userId, null);
        KycAmlClearance.IsClear(nodeLatest.Result).Should().BeFalse(
            "the node-direct verdict was never decided");
    }

    [Fact]
    public async Task ADecisionWithNoScreeningLedgerWired_IsRefusedRatherThanSilentlyDropped()
    {
        var decided = await NewManager(ApprovedStore(Guid.NewGuid()).Object, screening: null)
            .DecideAmlReviewAsync(
                Guid.NewGuid(), ClearRequest(Guid.NewGuid().ToString("N")), OperatorId);

        decided.IsError.Should().BeTrue();
        decided.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
    }

    // -- Who may work this desk ---------------------------------------------

    [Fact]
    public async Task TheQueueIsRefusedToAPrincipalThatIsNotTheNodeOperator()
    {
        // Both methods hang off an interface TenantKycConfigurationController also
        // injects, so the identity is demanded here and not inherited from the route.
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        screening.SeedResult(userId, null, KycScreeningDecision.HIT, lineageKey: "review-lineage");

        var page = await NewManager(ApprovedStore(userId).Object, screening)
            .ListAmlReviewsAsync(25, null, Guid.NewGuid());

        page.IsError.Should().BeTrue();
        page.Code.Should().Be(AzoaErrorCodes.Forbidden);
        page.Message.Should().Be(KycControlPlaneManager.OperatorOnlyReason);
    }

    [Fact]
    public async Task ADecisionByAPrincipalThatIsNotTheNodeOperator_WritesNothing()
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        var flagged = screening.SeedResult(userId, null, KycScreeningDecision.MANUAL_REVIEW, lineageKey: "review-lineage");

        var decided = await NewManager(ApprovedStore(userId).Object, screening)
            .DecideAmlReviewAsync(userId, ClearRequest(flagged.Id), Guid.NewGuid());

        decided.IsError.Should().BeTrue();
        decided.Code.Should().Be(AzoaErrorCodes.Forbidden);
        screening.SavedResults.Should().ContainSingle(
            "the counter did not move, not merely the door did not open");
    }

    // -- Helpers ------------------------------------------------------------

    private static AmlManualReviewDecisionRequest ClearRequest(string expectedScreeningId) => new()
    {
        Decision = AmlManualReviewDecisionRequest.Clear,
        ExpectedScreeningId = expectedScreeningId,
        Reason = Reason,
    };

    private static KycControlPlaneManager NewManager(
        IKycStore kycStore,
        IKycScreeningStore? screening,
        IConfiguration? configuration = null)
        => new(
            Mock.Of<IKycControlStore>(),
            kycStore,
            Mock.Of<IKycProviderRegistry>(),
            Mock.Of<IKycManager>(),
            Mock.Of<IApiKeyStore>(),
            Mock.Of<IUserStore>(),
            Mock.Of<IAdminBootstrapStateStore>(),
            Mock.Of<IHostEnvironment>(),
            configuration ?? new ConfigurationBuilder().Build(),
            screening);

    /// <summary>The production consumer gate, so "the gate re-opens" is not asserted on a stub.</summary>
    private static KycGateService NewGate(IKycStore store, IKycScreeningStore screening)
        => new(
            store,
            screening,
            ReadyProvider(),
            Options.Create(TrustedSettings()),
            Mock.Of<IHostEnvironment>(environment =>
                environment.EnvironmentName == Environments.Production));

    private static Mock<IKycStore> ApprovedStore(
        Guid userId,
        Guid? tenantId = null,
        DateTimeOffset? expiresAt = null)
    {
        var submission = new KycSubmission
        {
            Id = Guid.NewGuid().ToString("N"),
            AmlLineageKey = "review-lineage",
            UserId = userId.ToString("N"),
            TenantId = tenantId?.ToString("N"),
            Provider = KycProvider.VERIFF,
            ProviderKey = ProviderKey,
            ProviderResult = KycApprovalTrust.CreateEnvelope(new KycApprovalProfile(
                KycProvider.VERIFF, ProviderKey, "production-v1", "substantial")),
            Status = KycStatus.APPROVED,
            SubmittedAt = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddDays(30),
            CreatedDate = DateTimeOffset.UtcNow.AddDays(-1),
        };
        var store = new Mock<IKycStore>();
        store.Setup(item => item.GetEffectiveOperatorPageAsync(
                "approved", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IReadOnlyList<KycSubmission>>.Success([submission]));
        store.Setup(item => item.GetLatestSubmissionByUserAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<KycSubmission> { Result = submission });
        store.Setup(item => item.GetLatestSubmissionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<KycSubmission> { Result = submission });
        return store;
    }

    private static KycSettings TrustedSettings() => new()
    {
        Provider = ProviderKey,
        ApprovalPolicy = new KycApprovalPolicySettings
        {
            PolicyVersion = "production-v1",
            AssuranceLevel = "substantial",
            TrustedProviderKeys = [ProviderKey],
        },
    };

    private static IKycProviderService ReadyProvider()
    {
        var provider = new Mock<IKycProviderService>();
        provider.SetupGet(item => item.Provider).Returns(KycProvider.VERIFF);
        provider.SetupGet(item => item.ProviderKey).Returns(ProviderKey);
        provider.Setup(item => item.GetCapabilities()).Returns(new KycProviderCapabilitiesModel
        {
            Provider = KycProvider.VERIFF,
            ProviderKey = ProviderKey,
            Available = true,
        });
        return provider.Object;
    }

    private static IConfiguration Configure(string contact)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KycAmlClearance.ManualReviewContactConfigurationPath] = contact,
            })
            .Build();
}
