using FluentAssertions;
using Moq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Tests.Kyc;

public class KycManagerTests
{
    private readonly Mock<IKycStore> _store = new();
    private readonly Mock<IKycProviderService> _provider = new();
    private readonly KycManager _manager;

    public KycManagerTests()
    {
        _manager = new KycManager(
            _store.Object,
            _provider.Object,
            Options.Create(TrustedManualSettings()),
            DevelopmentEnvironment());

        // Defaults: no active submission, validation passes, session created, upsert echoes.
        _store.Setup(s => s.GetActiveSubmissionByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });
        _store.Setup(s => s.GetLatestSubmissionByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });
        _store.Setup(s => s.UpsertSubmissionAsync(It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((KycSubmission sub, CancellationToken _) => new AZOAResult<KycSubmission> { Result = sub });
        _store.Setup(s => s.TryRetireAttemptAsync(It.IsAny<KycSubmission>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((KycSubmission sub, DateTimeOffset now, CancellationToken _) =>
            {
                sub.Status = KycStatus.EXPIRED;
                return AZOAResult<KycSubmission>.Success(sub);
            });
        _store.Setup(s => s.CreateSubmissionAsync(
                It.IsAny<KycSubmission>(),
                It.IsAny<IReadOnlyList<KycDocument>>(),
                It.IsAny<CancellationToken>()))
              .ReturnsAsync((KycSubmission sub, IReadOnlyList<KycDocument> _, CancellationToken __) =>
                  new AZOAResult<KycSubmission> { Result = sub });
        _store.Setup(s => s.AttachDocumentsIfAbsentAsync(
                It.IsAny<KycSubmission>(),
                It.IsAny<IReadOnlyList<KycDocument>>(),
                It.IsAny<CancellationToken>()))
              .ReturnsAsync((KycSubmission sub, IReadOnlyList<KycDocument> _, CancellationToken __) =>
                  new AZOAResult<KycSubmission> { Result = sub });
        _store.Setup(s => s.TryReviewAsync(It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((KycSubmission sub, CancellationToken _) =>
                  new AZOAResult<KycSubmission> { Result = sub });
        _store.Setup(s => s.AddDocumentsAsync(It.IsAny<IEnumerable<KycDocument>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<bool> { Result = true });
        _store.Setup(s => s.GetDocumentsBySubmissionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<IEnumerable<KycDocument>>
              {
                  Result = new List<KycDocument>
                  {
                      new()
                      {
                          Id = Guid.NewGuid().ToString("N"),
                          SubmissionId = Guid.NewGuid().ToString("N"),
                          Type = KycDocumentType.GOVERNMENT_ID,
                          FileUrl = "https://blob/existing.png",
                          FileName = "existing.png",
                          CreatedDate = DateTimeOffset.UtcNow
                      }
                  }
              });
        _provider.Setup(p => p.ValidateDocumentsAsync(It.IsAny<IReadOnlyList<SubmitKycDocumentModel>>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<bool> { Result = true });
        _provider.SetupGet(p => p.Provider).Returns(KycProvider.MANUAL);
        _provider.SetupGet(p => p.ProviderKey).Returns("manual");
        _provider.Setup(p => p.GetCapabilities()).Returns(new KycProviderCapabilitiesModel
        {
            Provider = KycProvider.MANUAL,
            ProviderKey = "manual",
            Available = true,
            AcceptsDocumentReferences = true
        });
        _provider.Setup(p => p.CreateSessionAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<KycDocumentModel>>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((Guid id, IReadOnlyList<KycDocumentModel> _, CancellationToken __) => new AZOAResult<string> { Result = id.ToString("N") });
        _provider.Setup(p => p.BeginSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(AZOAResult<KycSessionStartModel>.Success(new KycSessionStartModel
                 {
                     Provider = KycProvider.MANUAL,
                     ProviderKey = "manual",
                     AcceptsDocumentReferences = true,
                     ExpiresAt = DateTime.UtcNow.AddMinutes(30),
                     Instructions = "Submit approved references."
                 }));
    }

    private static SubmitKycModel ValidSubmission() => new()
    {
        Documents = new List<SubmitKycDocumentModel>
        {
            new() { Type = KycDocumentType.GOVERNMENT_ID, FileUrl = "https://blob/doc.png", FileName = "doc.png", MimeType = "image/png", FileSizeBytes = 1024 }
        }
    };

    private static KycSubmission Stored(Guid id, Guid userId, KycStatus status) => new()
    {
        Id = id.ToString("N").ToLowerInvariant(),
        UserId = userId.ToString("N").ToLowerInvariant(),
        Provider = KycProvider.MANUAL,
        ProviderKey = "manual",
        ProviderResult = KycApprovalTrust.CreateEnvelope(ManualProfile()),
        Status = status,
        SubmittedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        CreatedDate = DateTimeOffset.UtcNow
    };

    private static KycSettings TrustedManualSettings() => new()
    {
        Provider = "manual",
        ApprovalPolicy = new KycApprovalPolicySettings
        {
            PolicyVersion = "dev-manual-v1",
            AssuranceLevel = "development-manual",
            TrustedProviderKeys = ["manual"],
            AllowManualInDevelopment = true,
        },
    };

    private static KycApprovalProfile ManualProfile() => new(
        KycProvider.MANUAL,
        "manual",
        "dev-manual-v1",
        "development-manual");

    private static IHostEnvironment DevelopmentEnvironment()
        => Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == Environments.Development);

    // Ã¢â€â‚¬Ã¢â€â‚¬ Begin / ensure-active session Ã¢â€â‚¬Ã¢â€â‚¬

    [Fact]
    public async Task Begin_FirstAttemptPersistsSecretBearingProviderStateServerSide()
    {
        var userId = Guid.NewGuid();
        _provider.Setup(p => p.BeginSessionAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSessionStartModel>.Success(new KycSessionStartModel
            {
                Provider = KycProvider.MANUAL,
                ProviderKey = "manual",
                ProviderSessionId = "internal-session",
                AcceptsDocumentReferences = true,
                ExpiresAt = DateTime.UtcNow.AddMinutes(30)
            }));

        var result = await _manager.BeginAsync(userId);

        result.IsError.Should().BeFalse();
        _store.Verify(store => store.CreateSubmissionAsync(
            It.Is<KycSubmission>(submission =>
                submission.UserId == userId.ToString("N")
                && submission.ProviderSessionId == "internal-session"
                && submission.Status == KycStatus.PENDING),
            It.Is<IReadOnlyList<KycDocument>>(documents => documents.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Begin_ExistingUnexpiredAttemptReplaysWithoutProviderCreate()
    {
        var userId = Guid.NewGuid();
        var active = Stored(Guid.NewGuid(), userId, KycStatus.PENDING);
        active.AcceptsDocumentReferences = true;
        active.SessionInstructions = "Resume this attempt.";
        active.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        _store.Setup(store => store.GetActiveSubmissionByUserAsync(
                userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(active));

        var result = await _manager.BeginAsync(userId);

        result.IsError.Should().BeFalse();
        result.Result!.Instructions.Should().Be("Resume this attempt.");
        _provider.Verify(provider => provider.BeginSessionAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(store => store.CreateSubmissionAsync(
            It.IsAny<KycSubmission>(),
            It.IsAny<IReadOnlyList<KycDocument>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Begin_ActiveAttemptWithRetiredProviderKeyIsExpiredNotReplayed()
    {
        var userId = Guid.NewGuid();
        var active = Stored(Guid.NewGuid(), userId, KycStatus.PENDING);
        active.ProviderKey = "retired-provider";
        active.ProviderResult = "{}";
        _store.Setup(store => store.GetActiveSubmissionByUserAsync(
                userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(active));

        var result = await _manager.BeginAsync(userId);

        result.IsError.Should().BeFalse();
        _store.Verify(store => store.TryRetireAttemptAsync(
            It.Is<KycSubmission>(submission => submission.Id == active.Id
                && submission.Status == KycStatus.EXPIRED),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _provider.Verify(provider => provider.BeginSessionAsync(
            userId, It.IsAny<CancellationToken>()), Times.Once);
        result.Result!.ProviderKey.Should().Be("manual");
    }

    // â”€â”€ Begin rollover / approval guard â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Begin_RejectedLatestAttemptCreatesNextServerOwnedAttempt()
    {
        var userId = Guid.NewGuid();
        _store.Setup(store => store.GetLatestSubmissionByUserAsync(
                userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(
                Stored(Guid.NewGuid(), userId, KycStatus.REJECTED)));

        var result = await _manager.BeginAsync(userId);

        result.IsError.Should().BeFalse();
        _provider.Verify(provider => provider.BeginSessionAsync(
            userId, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(store => store.CreateSubmissionAsync(
            It.Is<KycSubmission>(submission =>
                submission.UserId == userId.ToString("N")
                && submission.Status == KycStatus.PENDING),
            It.Is<IReadOnlyList<KycDocument>>(documents => documents.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Begin_LiveApprovalRefusesAnotherAttempt()
    {
        var userId = Guid.NewGuid();
        var approved = Stored(Guid.NewGuid(), userId, KycStatus.APPROVED);
        approved.ExpiresAt = DateTimeOffset.UtcNow.AddDays(1);
        _store.Setup(store => store.GetLatestSubmissionByUserAsync(
                userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(approved));

        var result = await _manager.BeginAsync(userId);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("KYC_ALREADY_APPROVED:");
        _provider.Verify(provider => provider.BeginSessionAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(store => store.CreateSubmissionAsync(
            It.IsAny<KycSubmission>(),
            It.IsAny<IReadOnlyList<KycDocument>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // â”€â”€ Submit â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Submit_HappyPath_PersistsPendingAndStampsSession()
    {
        var userId = Guid.NewGuid();

        var result = await _manager.SubmitAsync(ValidSubmission(), userId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(KycStatus.PENDING);
        result.Result.UserId.Should().Be(userId);
        result.Result.ProviderSessionId.Should().Be(userId.ToString("N"));
        _store.Verify(s => s.CreateSubmissionAsync(
            It.IsAny<KycSubmission>(),
            It.Is<IReadOnlyList<KycDocument>>(documents => documents.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Submit_ActiveSubmissionWithDocuments_ReturnsExistingAttempt()
    {
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetActiveSubmissionByUserAsync(userId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = Stored(Guid.NewGuid(), userId, KycStatus.PENDING) });

        var result = await _manager.SubmitAsync(ValidSubmission(), userId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(KycStatus.PENDING);
        _store.Verify(s => s.AttachDocumentsIfAbsentAsync(
            It.IsAny<KycSubmission>(),
            It.IsAny<IReadOnlyList<KycDocument>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.CreateSubmissionAsync(
            It.IsAny<KycSubmission>(),
            It.IsAny<IReadOnlyList<KycDocument>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_ActiveSessionWithoutDocuments_AttachesOnce()
    {
        var userId = Guid.NewGuid();
        var active = Stored(Guid.NewGuid(), userId, KycStatus.PENDING);
        active.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        _store.Setup(s => s.GetActiveSubmissionByUserAsync(userId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(AZOAResult<KycSubmission>.Success(active));
        _store.Setup(s => s.GetDocumentsBySubmissionAsync(
                Guid.ParseExact(active.Id, "N"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(AZOAResult<IEnumerable<KycDocument>>.Success(Array.Empty<KycDocument>()));

        var result = await _manager.SubmitAsync(ValidSubmission(), userId);

        result.IsError.Should().BeFalse();
        _store.Verify(s => s.AttachDocumentsIfAbsentAsync(
            It.Is<KycSubmission>(submission => submission.Id == active.Id),
            It.Is<IReadOnlyList<KycDocument>>(documents => documents.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        _provider.Verify(provider => provider.CreateSessionAsync(
            It.IsAny<Guid>(),
            It.IsAny<IReadOnlyList<KycDocumentModel>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_ActiveAttemptWithRetiredProviderKeyIsRejectedBeforeValidation()
    {
        var userId = Guid.NewGuid();
        var active = Stored(Guid.NewGuid(), userId, KycStatus.PENDING);
        active.ProviderKey = "retired-provider";
        _store.Setup(store => store.GetActiveSubmissionByUserAsync(
                userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(active));

        var result = await _manager.SubmitAsync(ValidSubmission(), userId);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("KYC_PROVIDER_CHANGED:");
        _provider.Verify(provider => provider.ValidateDocumentsAsync(
            It.IsAny<IReadOnlyList<SubmitKycDocumentModel>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_ActiveAttemptFromRetiredPolicyIsRejectedBeforeValidation()
    {
        var userId = Guid.NewGuid();
        var active = Stored(Guid.NewGuid(), userId, KycStatus.PENDING);
        active.ProviderResult = KycApprovalTrust.CreateEnvelope(new KycApprovalProfile(
            KycProvider.MANUAL,
            "manual",
            "retired-policy",
            "development-manual"));
        _store.Setup(store => store.GetActiveSubmissionByUserAsync(
                userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(active));

        var result = await _manager.SubmitAsync(ValidSubmission(), userId);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("KYC_POLICY_CHANGED:");
        _provider.Verify(provider => provider.ValidateDocumentsAsync(
            It.IsAny<IReadOnlyList<SubmitKycDocumentModel>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_TooManyDocuments_IsRejectedBeforeProviderWork()
    {
        var model = ValidSubmission();
        model.Documents = Enumerable.Range(0, KycDocumentRequestLimits.MaxDocuments + 1)
            .Select(_ => new SubmitKycDocumentModel
            {
                Type = KycDocumentType.GOVERNMENT_ID,
                FileUrl = "https://blob/doc.png",
                FileName = "doc.png",
            })
            .ToList();

        var result = await _manager.SubmitAsync(model, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain($"At most {KycDocumentRequestLimits.MaxDocuments}");
        _provider.Verify(provider => provider.ValidateDocumentsAsync(
            It.IsAny<IReadOnlyList<SubmitKycDocumentModel>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_OversizedMetadata_IsRejectedBeforeProviderWork()
    {
        var model = ValidSubmission();
        model.Documents.Single().Metadata =
            new string('x', KycDocumentRequestLimits.MaxMetadataCharacters + 1);

        var result = await _manager.SubmitAsync(model, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain(
            $"must not exceed {KycDocumentRequestLimits.MaxMetadataCharacters} characters");
        _provider.Verify(provider => provider.ValidateDocumentsAsync(
            It.IsAny<IReadOnlyList<SubmitKycDocumentModel>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_DocumentValidationFails_Rejected()
    {
        var userId = Guid.NewGuid();
        _provider.Setup(p => p.ValidateDocumentsAsync(It.IsAny<IReadOnlyList<SubmitKycDocumentModel>>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<bool> { IsError = true, Message = "bad doc" });

        var result = await _manager.SubmitAsync(ValidSubmission(), userId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("bad doc");
        _store.Verify(s => s.UpsertSubmissionAsync(It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_ProviderSessionUnavailable_WritesNothing()
    {
        _provider.Setup(p => p.CreateSessionAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyList<KycDocumentModel>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<string>.Failure("provider unavailable"));

        var result = await _manager.SubmitAsync(ValidSubmission(), Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("provider unavailable");
        _store.Verify(s => s.UpsertSubmissionAsync(It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.CreateSubmissionAsync(
            It.IsAny<KycSubmission>(),
            It.IsAny<IReadOnlyList<KycDocument>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.AddDocumentsAsync(It.IsAny<IEnumerable<KycDocument>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_IgnoresCallerSuppliedUserId()
    {
        var authenticated = Guid.NewGuid();
        var forged = Guid.NewGuid();
        var model = ValidSubmission();
        model.UserId = forged;

        var result = await _manager.SubmitAsync(model, authenticated);

        result.Result!.UserId.Should().Be(authenticated);
    }

    // â”€â”€ GetStatus â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task GetStatus_ReturnsLatest()
    {
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetLatestSubmissionByUserAsync(userId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = Stored(Guid.NewGuid(), userId, KycStatus.APPROVED) });

        var result = await _manager.GetStatusAsync(userId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(KycStatus.APPROVED);
    }

    [Fact]
    public async Task GetStatus_ApprovalFromRetiredPolicyProjectsExpired()
    {
        var userId = Guid.NewGuid();
        var approval = Stored(Guid.NewGuid(), userId, KycStatus.APPROVED);
        approval.ProviderResult = KycApprovalTrust.CreateEnvelope(new KycApprovalProfile(
            KycProvider.MANUAL,
            "manual",
            "retired-policy",
            "development-manual"));
        _store.Setup(store => store.GetLatestSubmissionByUserAsync(
                userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(approval));

        var result = await _manager.GetStatusAsync(userId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(KycStatus.EXPIRED);
    }

    [Fact]
    public async Task GetStatus_None_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        _store.Setup(s => s.GetLatestSubmissionByUserAsync(userId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });

        var result = await _manager.GetStatusAsync(userId);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(KycAuthorizationError.NotFound);
    }

    // â”€â”€ Approve / Reject â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Approve_ConditionallyCommitsLedgerDecision()
    {
        var userId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        var stored = Stored(submissionId, userId, KycStatus.PENDING);
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = stored });

        var result = await _manager.ApproveAsync(submissionId, reviewerId, "looks good");

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(KycStatus.APPROVED);
        _store.Verify(s => s.TryReviewAsync(
            It.Is<KycSubmission>(submission => submission.Status == KycStatus.APPROVED),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Approve_StampsTheApprovalClockNotTheSubmissionClock()
    {
        // The attempt was stamped with the SUBMISSION clock (three days). Approving
        // it must move the row onto the APPROVAL clock, or every verified sponsor is
        // pushed back through identity check when the attempt window lapses.
        var submissionId = Guid.NewGuid();
        var stored = Stored(submissionId, Guid.NewGuid(), KycStatus.PENDING);
        stored.ExpiresAt = DateTimeOffset.UtcNow.AddDays(3);
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = stored });

        var floor = DateTimeOffset.UtcNow.AddDays(KycApprovalValidity.DefaultDays - 1);
        var result = await _manager.ApproveAsync(submissionId, Guid.NewGuid(), null);

        result.IsError.Should().BeFalse(result.Message);
        _store.Verify(s => s.TryReviewAsync(
            It.Is<KycSubmission>(submission =>
                submission.Status == KycStatus.APPROVED
                && submission.ExpiresAt > floor),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Approve_HonoursAConfiguredApprovalValidity()
    {
        var submissionId = Guid.NewGuid();
        var stored = Stored(submissionId, Guid.NewGuid(), KycStatus.PENDING);
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = stored });
        var manager = new KycManager(
            _store.Object,
            _provider.Object,
            Options.Create(TrustedManualSettings()),
            DevelopmentEnvironment(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [KycApprovalValidity.ConfigurationKey] = "30",
                })
                .Build());

        var ceiling = DateTimeOffset.UtcNow.AddDays(31);
        var result = await manager.ApproveAsync(submissionId, Guid.NewGuid(), null);

        result.IsError.Should().BeFalse(result.Message);
        _store.Verify(s => s.TryReviewAsync(
            It.Is<KycSubmission>(submission => submission.ExpiresAt < ceiling),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null, KycApprovalValidity.DefaultDays)]
    [InlineData("", KycApprovalValidity.DefaultDays)]
    [InlineData("0", KycApprovalValidity.DefaultDays)]
    [InlineData("-5", KycApprovalValidity.DefaultDays)]
    [InlineData("30", 30)]
    [InlineData("99999", KycApprovalValidity.MaximumDays)]
    public void ApprovalValidity_DefaultsAndClamps(string? configured, int expectedDays)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KycApprovalValidity.ConfigurationKey] = configured,
            })
            .Build();

        KycApprovalValidity.ResolveDays(configuration).Should().Be(expectedDays);
    }

    [Fact]
    public void ApprovalValidity_WithoutConfiguration_IsTwelveMonths()
        => KycApprovalValidity.ResolveDays(null).Should().Be(365);

    [Fact]
    public async Task Reject_SetsRejectedWithReason()
    {
        var userId = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = Stored(submissionId, userId, KycStatus.PENDING) });

        var result = await _manager.RejectAsync(submissionId, Guid.NewGuid(), "notes", "blurry id");

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(KycStatus.REJECTED);
        result.Result.RejectionReason.Should().Be("blurry id");
        _store.Verify(s => s.TryReviewAsync(
            It.Is<KycSubmission>(submission => submission.Status == KycStatus.REJECTED),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Approve_AlreadyTerminal_ReturnsError()
    {
        var submissionId = Guid.NewGuid();
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = Stored(submissionId, Guid.NewGuid(), KycStatus.APPROVED) });

        var result = await _manager.ApproveAsync(submissionId, Guid.NewGuid(), null);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Cannot approve");
    }

    [Fact]
    public async Task Approve_ConcurrentDecisionWonByAnotherReviewer_ReturnsConflict()
    {
        var submissionId = Guid.NewGuid();
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission>
              {
                  Result = Stored(submissionId, Guid.NewGuid(), KycStatus.PENDING)
              });
        _store.Setup(s => s.TryReviewAsync(It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });

        var result = await _manager.ApproveAsync(submissionId, Guid.NewGuid(), null);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("already been decided");
        _store.Verify(s => s.TryReviewAsync(
            It.Is<KycSubmission>(submission => submission.Status == KycStatus.APPROVED),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Approve_BeginCreatedAttemptWithoutDocuments_NeverReviews()
    {
        var submissionId = Guid.NewGuid();
        _store.Setup(store => store.GetSubmissionByIdAsync(
                submissionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(
                Stored(submissionId, Guid.NewGuid(), KycStatus.PENDING)));
        _store.Setup(store => store.GetDocumentsBySubmissionAsync(
                submissionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<KycDocument>>.Success(Array.Empty<KycDocument>()));

        var result = await _manager.ApproveAsync(submissionId, Guid.NewGuid(), null);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("document references");
        _store.Verify(store => store.TryReviewAsync(
            It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Approve_ReviewStoreOutage_PreservesDependencyCode()
    {
        var submissionId = Guid.NewGuid();
        _store.Setup(store => store.GetSubmissionByIdAsync(
                submissionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(
                Stored(submissionId, Guid.NewGuid(), KycStatus.PENDING)));
        _store.Setup(store => store.TryReviewAsync(
                It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.FailureWithCode(
                "KYC persistence is temporarily unavailable.",
                AzoaErrorCodes.DependencyUnavailable));

        var result = await _manager.ApproveAsync(submissionId, Guid.NewGuid(), null);

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
    }

    [Fact]
    public async Task Approve_ExternalProviderSubmission_IsRejected()
    {
        var submissionId = Guid.NewGuid();
        var stored = Stored(submissionId, Guid.NewGuid(), KycStatus.PENDING);
        stored.Provider = KycProvider.VERIFF;
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = stored });

        var result = await _manager.ApproveAsync(submissionId, Guid.NewGuid(), null);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("verified provider event");
        _store.Verify(
            s => s.TryReviewAsync(It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Approve_RetiredPolicyAttempt_IsRejected()
    {
        var submissionId = Guid.NewGuid();
        var stored = Stored(submissionId, Guid.NewGuid(), KycStatus.PENDING);
        stored.ProviderResult = KycApprovalTrust.CreateEnvelope(new KycApprovalProfile(
            KycProvider.MANUAL,
            "manual",
            "retired-policy",
            "development-manual"));
        _store.Setup(store => store.GetSubmissionByIdAsync(
                submissionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(stored));

        var result = await _manager.ApproveAsync(submissionId, Guid.NewGuid(), null);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("KYC_POLICY_CHANGED:");
        _store.Verify(store => store.TryReviewAsync(
            It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reject_AlreadyTerminal_ReturnsError()
    {
        var submissionId = Guid.NewGuid();
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = Stored(submissionId, Guid.NewGuid(), KycStatus.REJECTED) });

        var result = await _manager.RejectAsync(submissionId, Guid.NewGuid(), null, null);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Cannot reject");
    }

    [Fact]
    public async Task Approve_NotFound_ReturnsNotFoundMarker()
    {
        var submissionId = Guid.NewGuid();
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });

        var result = await _manager.ApproveAsync(submissionId, Guid.NewGuid(), null);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(KycAuthorizationError.NotFound);
    }

    // â”€â”€ IDOR â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task GetById_DifferentUser_ReturnsForbidden()
    {
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = Stored(submissionId, owner, KycStatus.PENDING) });

        var result = await _manager.GetByIdAsync(submissionId, attacker);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(KycAuthorizationError.Forbidden);
    }

    [Fact]
    public async Task GetById_Owner_Succeeds()
    {
        var owner = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = Stored(submissionId, owner, KycStatus.PENDING) });

        var result = await _manager.GetByIdAsync(submissionId, owner);

        result.IsError.Should().BeFalse();
        result.Result!.Id.Should().Be(submissionId);
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsNotFound()
    {
        var submissionId = Guid.NewGuid();
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });

        var result = await _manager.GetByIdAsync(submissionId, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(KycAuthorizationError.NotFound);
    }

    [Fact]
    public async Task ListDocuments_DifferentUser_ReturnsForbidden()
    {
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = Stored(submissionId, owner, KycStatus.PENDING) });

        var result = await _manager.ListDocumentsAsync(submissionId, attacker);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(KycAuthorizationError.Forbidden);
        _store.Verify(s => s.GetDocumentsBySubmissionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListDocuments_Owner_ReturnsDocuments()
    {
        var owner = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = Stored(submissionId, owner, KycStatus.PENDING) });
        _store.Setup(s => s.GetDocumentsBySubmissionAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<IEnumerable<KycDocument>>
              {
                  Result = new List<KycDocument>
                  {
                      new() { Id = Guid.NewGuid().ToString("N"), SubmissionId = submissionId.ToString("N"), Type = KycDocumentType.SELFIE, FileUrl = "u", FileName = "f" }
                  }
              });

        var result = await _manager.ListDocumentsAsync(submissionId, owner);

        result.IsError.Should().BeFalse();
        result.Result.Should().HaveCount(1);
    }
}
