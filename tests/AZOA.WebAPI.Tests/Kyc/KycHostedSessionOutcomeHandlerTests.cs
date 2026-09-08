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
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Tests.Kyc;

/// <summary>
/// Identity and sanctions are separate facts. An adverse AML verdict must not rewrite
/// the identity verdict, must not be disclosed, and must leave a review open — the
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
        _binding.Verify(binding => binding.BindAsync(
                SubjectUserId,
                TenantId,
                KycProvider.IDENTOMAT,
                ProviderKey,
                It.IsAny<HostedSessionSubjectAttributes>(),
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
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycScreeningResult>.Failure("screening provider unavailable"));

        var resumed = await NewHandler().ResumeAsync(session, ApprovedOutcome());

        resumed.IsError.Should().BeTrue();
        _binding.VerifyNoOtherCalls();
        _saved.Should().BeNull();
    }

    // ── Fixtures ───────────────────────────────────────────────────────────

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

    private void SeedActiveSubmission(HostedSession session)
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
            SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        _kycStore.Setup(store => store.GetActiveSubmissionAsync(
                SubjectUserId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(submission));
        _kycStore.Setup(store => store.UpsertSubmissionAsync(
                It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((KycSubmission row, CancellationToken _) =>
            {
                _saved = row;
                return AZOAResult<KycSubmission>.Success(row);
            });
    }

    private void SeedScreening(KycScreeningDecision decision, bool resultsTruncated)
    {
        _screening.Setup(service => service.ScreenAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycScreeningResult>.Success(new KycScreeningResult
            {
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
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycScreeningMonitor>.Success(new KycScreeningMonitor
            {
                Id = Guid.NewGuid().ToString("N"),
                ProviderMonitorId = "monitor-1"
            }));
        _binding.Setup(binding => binding.BindAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<KycProvider>(),
                It.IsAny<string>(),
                It.IsAny<HostedSessionSubjectAttributes>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycIdentity>.Success(new KycIdentity
            {
                Id = Guid.NewGuid().ToString("N")
            }));
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
