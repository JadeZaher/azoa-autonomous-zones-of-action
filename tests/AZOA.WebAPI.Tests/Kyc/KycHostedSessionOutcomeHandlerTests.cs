// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Tests.Kyc;

/// <summary>
/// Identity and sanctions are separate facts. An adverse AML verdict must not rewrite
/// the identity verdict, must not be disclosed, and must leave a review open â€” the
/// recourse a false-positive name match depends on.
/// </summary>
public sealed class KycHostedSessionOutcomeHandlerTests
{
    private const string ProviderKey = "identomat";
    private static readonly Guid SubjectUserId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();

    private readonly Mock<IKycStore> _kycStore = new();
    private readonly Mock<IKycIdentityBindingService> _binding = new();
    private readonly Mock<IIdentomatScreeningService> _screening = new();
    private readonly RecordingLogger _logger = new();
    private KycSubmission? _saved;

    [Theory]
    [InlineData(KycScreeningDecision.MANUAL_REVIEW, false)]
    [InlineData(KycScreeningDecision.HIT, false)]
    [InlineData(KycScreeningDecision.NO_HIT, true)]
    public async Task AnAdverseOrIncompleteScreening_StillRecordsTheVerifiedIdentity(
        KycScreeningDecision decision,
        bool resultsTruncated)
    {
        // The submission records that IDENTITY was verified, which is true. The gate
        // reads the screening row separately; rewriting the submission to REJECTED
        // would tell the subject their documents failed, which they did not.
        var session = NewSession();
        SeedActiveSubmission(session);
        SeedScreening(decision, resultsTruncated);

        var resumed = await NewHandler().ResumeAsync(session, ApprovedOutcome());

        resumed.IsError.Should().BeFalse();
        _saved!.Status.Should().Be(KycStatus.APPROVED);
        _saved.RejectionReason.Should().BeNull("an AML finding is never disclosed on the ledger row");
        _binding.Verify(binding => binding.BindHostedAsync(
                SubjectUserId,
                TenantId,
                KycProvider.IDENTOMAT,
                ProviderKey,
                It.IsAny<HostedSessionSubjectAttributes>(),
                It.IsAny<KycSubmission>(), It.IsAny<HostedSession>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(KycScreeningDecision.MANUAL_REVIEW, false)]
    [InlineData(KycScreeningDecision.HIT, false)]
    [InlineData(KycScreeningDecision.NO_HIT, true)]
    public async Task AnUnclearedScreening_OpensAManualReview(
        KycScreeningDecision decision,
        bool resultsTruncated)
    {
        // A NO_HIT over a TRUNCATED hit list is not clearance either, which is why the
        // handler asks KycAmlClearance rather than comparing the decision itself.
        var session = NewSession();
        SeedActiveSubmission(session);
        SeedScreening(decision, resultsTruncated);

        await NewHandler().ResumeAsync(session, ApprovedOutcome());

        _logger.Warnings.Should().ContainSingle(entry => entry.Contains("AML manual review opened"));
    }

    [Fact]
    public async Task ACleanCompleteScreening_OpensNoReview()
    {
        var session = NewSession();
        SeedActiveSubmission(session);
        SeedScreening(KycScreeningDecision.NO_HIT, resultsTruncated: false);

        await NewHandler().ResumeAsync(session, ApprovedOutcome());

        _logger.Warnings.Should().NotContain(entry => entry.Contains("AML manual review opened"));
    }

    [Fact]
    public async Task AFailedScreening_NeverBecomesABinding()
    {
        // The screening row is what the value gate reads; no row means no answer, so
        // an approval that could not be screened must not become an identity either.
        var session = NewSession();
        SeedActiveSubmission(session);
        _screening.Setup(service => service.ScreenAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(AZOAResult<KycScreeningResult>.Failure("screening provider unavailable"));

        var resumed = await NewHandler().ResumeAsync(session, ApprovedOutcome());

        resumed.IsError.Should().BeTrue();
        _binding.VerifyNoOtherCalls();
        _saved.Should().BeNull();
    }

    // â”€â”€ Fixtures â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [InlineData("session")]
    [InlineData("provider")]
    [InlineData("user")]
    [InlineData("tenant")]
    [InlineData("expired")]
    [InlineData("terminal")]
    public async Task StaleOrMismatchedAttempt_PerformsNoIdentityEffects(string mismatch)
    {
        var session = NewSession();
        var submission = SeedActiveSubmission(session);
        switch (mismatch)
        {
            case "session": submission.ProviderSessionId = Guid.NewGuid().ToString("N"); break;
            case "provider": submission.ProviderKey = "other"; break;
            case "user": submission.UserId = Guid.NewGuid().ToString("N"); break;
            case "tenant": submission.TenantId = null; break;
            case "expired": submission.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); break;
            case "terminal": submission.Status = KycStatus.REJECTED; break;
        }

        var result = await NewHandler().ResumeAsync(session, ApprovedOutcome());

        // A retired/superseded attempt ("session", "expired") terminalizes the stale session quietly.
        result.IsError.Should().Be(mismatch is not ("expired" or "session"));
        result.Result.Should().BeNull();
        _binding.VerifyNoOtherCalls();
        _screening.VerifyNoOtherCalls();
        _saved.Should().BeNull();
    }

    [Fact]
    public async Task MissingAttempt_PerformsNoIdentityEffects()
    {
        _kycStore.Setup(store => store.GetActiveSubmissionAsync(
                SubjectUserId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(null!));

        _kycStore.Setup(store => store.GetLatestSubmissionAsync(SubjectUserId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(null!));
        var result = await NewHandler().ResumeAsync(NewSession(), ApprovedOutcome());

        result.IsError.Should().BeTrue();
        _binding.VerifyNoOtherCalls();
        _screening.VerifyNoOtherCalls();
        _saved.Should().BeNull();
    }

    [Fact]
    public async Task LostCompletionRace_DoesNotMutateTheObservedSubmission()
    {
        var session = NewSession();
        var submission = SeedActiveSubmission(session);
        var originalExpiry = submission.ExpiresAt;
        SeedScreening(KycScreeningDecision.NO_HIT, false);
        _kycStore.Setup(store => store.TryCompleteHostedAsync(
                It.IsAny<KycSubmission>(), It.IsAny<HostedSession>(), It.IsAny<KycStatus>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(null!));

        var result = await NewHandler().ResumeAsync(session, ApprovedOutcome());

        // Lost CAS is retryable: the flow releases the lease and the next resume converges.
        result.IsError.Should().BeTrue("a lost completion must never close the session without the decision");
        result.Code.Should().Be(AzoaErrorCodes.Conflict);
        submission.Status.Should().Be(KycStatus.PENDING);
        submission.ExpiresAt.Should().Be(originalExpiry);
        _kycStore.Verify(store => store.UpsertSubmissionAsync(
            It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public static TheoryData<string, HostedSessionOutcome, bool> ProgressOutcomes => new()
    {
        // Veriff: verification null = the applicant has not submitted yet.
        { "veriff-pending", new HostedSessionOutcome { SessionExists = true, Outcome = "PENDING" }, false },
        { "veriff-resubmission", new HostedSessionOutcome { SessionExists = true, Outcome = "RESUBMISSION_REQUESTED" }, false },
        // Generic hosted v1 "review" carries no manual-review contract.
        { "generic-review", new HostedSessionOutcome { SessionExists = true, Outcome = "REVIEW" }, false },
        { "identomat-undecided", IdentomatKycProviderService.MapOutcome(new IdentomatResultResponse { Result = null }), false },
        { "identomat-more-info", IdentomatKycProviderService.MapOutcome(new IdentomatResultResponse { Result = "ADDITIONAL_INFORMATION_REQUESTED" }), false },
        { "identomat-manual-check", IdentomatKycProviderService.MapOutcome(new IdentomatResultResponse { Result = "MANUAL_CHECK" }), true },
    };

    [Theory]
    [MemberData(nameof(ProgressOutcomes))]
    public async Task Only_a_vendor_manual_review_moves_the_attempt_to_review_and_extends_it(
        string source, HostedSessionOutcome outcome, bool extended)
    {
        _ = source;
        var progress = _kycStore.As<IKycHostedProgressStore>();
        var session = NewSession();
        var submission = SeedActiveSubmission(session);
        var originalExpiry = submission.ExpiresAt!.Value;
        progress.Setup(store => store.TryMarkHostedInReviewAsync(It.IsAny<KycSubmission>(), It.IsAny<HostedSession>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((KycSubmission row, HostedSession _, DateTimeOffset _, DateTimeOffset until, CancellationToken _) =>
            {
                row.Status = KycStatus.IN_REVIEW;
                row.ExpiresAt = until;
                return AZOAResult<KycSubmission>.Success(row);
            });

        await NewHandler().ObserveProgressAsync(session, outcome);

        // RV-L2 L2-1: an unsubmitted or abandoned start must keep its short lifetime.
        submission.Status.Should().Be(extended ? KycStatus.IN_REVIEW : KycStatus.PENDING);
        if (extended)
            submission.ExpiresAt.Should().BeAfter(originalExpiry.AddDays(1));
        else
            submission.ExpiresAt.Should().Be(originalExpiry);
    }

    [Theory]
    [InlineData("open", "Live")]
    [InlineData("in-review", "Live")]
    [InlineData("expired", "Dead")]
    [InlineData("retired", "Dead")]
    [InlineData("decided", "Dead")]
    [InlineData("other-session", "Dead")]
    [InlineData("missing", "Dead")]
    public async Task The_poll_probe_reports_whether_a_verdict_could_still_be_applied(string attemptState, string expected)
    {
        var session = NewSession();
        var submission = new KycSubmission
        {
            Id = session.Id,
            UserId = SubjectUserId.ToString("N"),
            TenantId = TenantId.ToString("N"),
            Provider = KycProvider.IDENTOMAT,
            ProviderKey = ProviderKey,
            ProviderSessionId = session.Id,
            Status = KycStatus.PENDING,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(25),
        };
        switch (attemptState)
        {
            case "in-review": submission.Status = KycStatus.IN_REVIEW; submission.ExpiresAt = DateTimeOffset.UtcNow.AddDays(29); break;
            case "expired": submission.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); break;
            case "retired": submission.Status = KycStatus.EXPIRED; break;
            case "decided": submission.Status = KycStatus.APPROVED; break;
            case "other-session": submission.ProviderSessionId = Guid.NewGuid().ToString("N"); break;
        }
        _kycStore.Setup(store => store.GetSubmissionByIdAsync(Guid.ParseExact(session.Id, "N"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(attemptState == "missing" ? null! : submission));

        var relevance = await NewHandler().ProbeAsync(session, DateTimeOffset.UtcNow);

        relevance.ToString().Should().Be(expected);
    }

    private static HostedSession NewSession() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        FlowKind = "kyc",
        ProviderKey = ProviderKey,
        SubjectUserId = SubjectUserId.ToString("N"),
        TenantId = TenantId.ToString("N"),
        State = "COMPLETED"
    };

    private static HostedSessionOutcome ApprovedOutcome() => new()
    {
        Outcome = "APPROVED",
        IsTerminal = true,
        IsApproved = true,
        Subject = new HostedSessionSubjectAttributes
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            Birthday = "1815-12-10"
        }
    };

    private KycSubmission SeedActiveSubmission(HostedSession session)
    {
        var submission = new KycSubmission
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = SubjectUserId.ToString("N"),
            TenantId = TenantId.ToString("N"),
            Provider = KycProvider.IDENTOMAT,
            ProviderKey = ProviderKey,
            ProviderSessionId = session.Id,
            Status = KycStatus.PENDING,
            SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(25)
        };
        _kycStore.Setup(store => store.GetActiveSubmissionAsync(
                SubjectUserId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(submission));
        _kycStore.Setup(store => store.TryCompleteHostedAsync(
                It.IsAny<KycSubmission>(), It.IsAny<HostedSession>(), It.IsAny<KycStatus>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((KycSubmission row, HostedSession lease, KycStatus status, DateTimeOffset reviewed, DateTimeOffset? expiry, string? reason, CancellationToken _) =>
            {
                row.Status = status;
                row.ReviewedAt = reviewed;
                row.ExpiresAt = expiry;
                row.RejectionReason = reason;
                _saved = row;
                return AZOAResult<KycSubmission>.Success(row);
            });
        return submission;
    }

    private void SeedScreening(KycScreeningDecision decision, bool resultsTruncated)
    {
        _screening.Setup(service => service.ScreenAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid user, Guid? tenant, string name, string? birthday, CancellationToken ct, string? lineageKey) => AZOAResult<KycScreeningResult>.Success(new KycScreeningResult
            {
                LineageKey = lineageKey,
                Id = Guid.NewGuid().ToString("N"),
                SubjectUserId = SubjectUserId.ToString("N"),
                TenantId = TenantId.ToString("N"),
                Decision = decision,
                ResultsTruncated = resultsTruncated,
                PolicyVersion = KycScreeningRiskPolicy.CurrentPolicyVersion,
                ScreenedAt = DateTimeOffset.UtcNow
            }));
        _screening.Setup(service => service.RegisterMonitorAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid user, Guid? tenant, string name, string? birthday, string lineageKey, CancellationToken ct) => AZOAResult<KycScreeningMonitor>.Success(new KycScreeningMonitor
            {
                Id = Guid.NewGuid().ToString("N"),
                ProviderMonitorId = "monitor-1", LineageKey = lineageKey
            }));
        _binding.Setup(binding => binding.BindHostedAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<KycProvider>(),
                It.IsAny<string>(),
                It.IsAny<HostedSessionSubjectAttributes>(),
                It.IsAny<KycSubmission>(), It.IsAny<HostedSession>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycIdentity>.Success(new KycIdentity
            {
                Id = Guid.NewGuid().ToString("N")
            }));
    }

    [Fact]
    public async Task CallbackBeforeSubmission_IsRetryableAndLaterApprovalRetainsMonitoringLineage()
    {
        var session = NewSession();
        _kycStore.Setup(store => store.GetActiveSubmissionAsync(SubjectUserId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(null!));
        _kycStore.Setup(store => store.GetLatestSubmissionAsync(SubjectUserId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(null!));
        (await NewHandler().ResumeAsync(session, ApprovedOutcome())).IsError.Should().BeTrue();
        _saved.Should().BeNull();
        SeedActiveSubmission(session);
        SeedScreening(KycScreeningDecision.NO_HIT, false);
        (await NewHandler().ResumeAsync(session, ApprovedOutcome())).IsError.Should().BeFalse();
        _saved!.AmlLineageKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task MonitorRegistrationFailureLeavesApprovalRetryable()
    {
        var session = NewSession();
        SeedActiveSubmission(session);
        SeedScreening(KycScreeningDecision.NO_HIT, false);
        _screening.Setup(service => service.RegisterMonitorAsync(
            It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycScreeningMonitor>.Failure("Temporarily unavailable"));

        var result = await NewHandler().ResumeAsync(session, ApprovedOutcome());

        result.IsError.Should().BeTrue();
        _saved.Should().BeNull();
        _kycStore.Verify(store => store.TryCompleteHostedAsync(It.IsAny<KycSubmission>(), It.IsAny<HostedSession>(),
            It.IsAny<KycStatus>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private KycHostedSessionOutcomeHandler NewHandler() => new(
        _kycStore.Object,
        _binding.Object,
        _screening.Object,
        _logger);

    /// <summary>Captures formatted warnings so a policy branch is observable.</summary>
    private sealed class RecordingLogger : ILogger<KycHostedSessionOutcomeHandler>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
