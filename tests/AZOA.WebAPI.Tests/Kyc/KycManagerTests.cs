using FluentAssertions;
using Moq;
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
        _store.Setup(s => s.GetActiveSubmissionByAvatarAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });
        _store.Setup(s => s.GetLatestSubmissionByAvatarAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });
        _store.Setup(s => s.UpsertSubmissionAsync(It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((KycSubmission sub, CancellationToken _) => new AZOAResult<KycSubmission> { Result = sub });
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

    private static KycSubmission Stored(Guid id, Guid avatarId, KycStatus status) => new()
    {
        Id = id.ToString("N").ToLowerInvariant(),
        AvatarId = avatarId.ToString("N").ToLowerInvariant(),
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

    // â”€â”€ Begin / ensure-active session â”€â”€

    [Fact]
    public async Task Begin_FirstAttemptPersistsSecretBearingProviderStateServerSide()
    {
        var avatarId = Guid.NewGuid();
        _provider.Setup(p => p.BeginSessionAsync(avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSessionStartModel>.Success(new KycSessionStartModel
            {
                Provider = KycProvider.MANUAL,
                ProviderKey = "manual",
                ProviderSessionId = "internal-session",
                AcceptsDocumentReferences = true,
                ExpiresAt = DateTime.UtcNow.AddMinutes(30)
            }));

        var result = await _manager.BeginAsync(avatarId);

        result.IsError.Should().BeFalse();
        _store.Verify(store => store.CreateSubmissionAsync(
            It.Is<KycSubmission>(submission =>
                submission.AvatarId == avatarId.ToString("N")
                && submission.ProviderSessionId == "internal-session"
                && submission.Status == KycStatus.PENDING),
            It.Is<IReadOnlyList<KycDocument>>(documents => documents.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Begin_ExistingUnexpiredAttemptReplaysWithoutProviderCreate()
    {
        var avatarId = Guid.NewGuid();
        var active = Stored(Guid.NewGuid(), avatarId, KycStatus.PENDING);
        active.AcceptsDocumentReferences = true;
        active.SessionInstructions = "Resume this attempt.";
        active.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        _store.Setup(store => store.GetActiveSubmissionByAvatarAsync(
                avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(active));

        var result = await _manager.BeginAsync(avatarId);

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
        var avatarId = Guid.NewGuid();
        var active = Stored(Guid.NewGuid(), avatarId, KycStatus.PENDING);
        active.ProviderKey = "retired-provider";
        active.ProviderResult = "{}";
        _store.Setup(store => store.GetActiveSubmissionByAvatarAsync(
                avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(active));

        var result = await _manager.BeginAsync(avatarId);

        result.IsError.Should().BeFalse();
        _store.Verify(store => store.UpsertSubmissionAsync(
            It.Is<KycSubmission>(submission => submission.Id == active.Id
                && submission.Status == KycStatus.EXPIRED),
            It.IsAny<CancellationToken>()), Times.Once);
        _provider.Verify(provider => provider.BeginSessionAsync(
            avatarId, It.IsAny<CancellationToken>()), Times.Once);
        result.Result!.ProviderKey.Should().Be("manual");
    }

    // ── Begin rollover / approval guard ──────────────────────────────────────

    [Fact]
    public async Task Begin_RejectedLatestAttemptCreatesNextServerOwnedAttempt()
    {
        var avatarId = Guid.NewGuid();
        _store.Setup(store => store.GetLatestSubmissionByAvatarAsync(
                avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(
                Stored(Guid.NewGuid(), avatarId, KycStatus.REJECTED)));

        var result = await _manager.BeginAsync(avatarId);

        result.IsError.Should().BeFalse();
        _provider.Verify(provider => provider.BeginSessionAsync(
            avatarId, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(store => store.CreateSubmissionAsync(
            It.Is<KycSubmission>(submission =>
                submission.AvatarId == avatarId.ToString("N")
                && submission.Status == KycStatus.PENDING),
            It.Is<IReadOnlyList<KycDocument>>(documents => documents.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Begin_LiveApprovalRefusesAnotherAttempt()
    {
        var avatarId = Guid.NewGuid();
        var approved = Stored(Guid.NewGuid(), avatarId, KycStatus.APPROVED);
        approved.ExpiresAt = DateTimeOffset.UtcNow.AddDays(1);
        _store.Setup(store => store.GetLatestSubmissionByAvatarAsync(
                avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(approved));

        var result = await _manager.BeginAsync(avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("KYC_ALREADY_APPROVED:");
        _provider.Verify(provider => provider.BeginSessionAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(store => store.CreateSubmissionAsync(
            It.IsAny<KycSubmission>(),
            It.IsAny<IReadOnlyList<KycDocument>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Submit ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_HappyPath_PersistsPendingAndStampsSession()
    {
        var avatarId = Guid.NewGuid();

        var result = await _manager.SubmitAsync(ValidSubmission(), avatarId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(KycStatus.PENDING);
        result.Result.AvatarId.Should().Be(avatarId);
        result.Result.ProviderSessionId.Should().Be(avatarId.ToString("N"));
        _store.Verify(s => s.CreateSubmissionAsync(
            It.IsAny<KycSubmission>(),
            It.Is<IReadOnlyList<KycDocument>>(documents => documents.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Submit_ActiveSubmissionWithDocuments_ReturnsExistingAttempt()
    {
        var avatarId = Guid.NewGuid();
        _store.Setup(s => s.GetActiveSubmissionByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = Stored(Guid.NewGuid(), avatarId, KycStatus.PENDING) });

        var result = await _manager.SubmitAsync(ValidSubmission(), avatarId);

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
        var avatarId = Guid.NewGuid();
        var active = Stored(Guid.NewGuid(), avatarId, KycStatus.PENDING);
        active.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        _store.Setup(s => s.GetActiveSubmissionByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(AZOAResult<KycSubmission>.Success(active));
        _store.Setup(s => s.GetDocumentsBySubmissionAsync(
                Guid.ParseExact(active.Id, "N"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(AZOAResult<IEnumerable<KycDocument>>.Success(Array.Empty<KycDocument>()));

        var result = await _manager.SubmitAsync(ValidSubmission(), avatarId);

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
        var avatarId = Guid.NewGuid();
        var active = Stored(Guid.NewGuid(), avatarId, KycStatus.PENDING);
        active.ProviderKey = "retired-provider";
        _store.Setup(store => store.GetActiveSubmissionByAvatarAsync(
                avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(active));

        var result = await _manager.SubmitAsync(ValidSubmission(), avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith("KYC_PROVIDER_CHANGED:");
        _provider.Verify(provider => provider.ValidateDocumentsAsync(
            It.IsAny<IReadOnlyList<SubmitKycDocumentModel>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_ActiveAttemptFromRetiredPolicyIsRejectedBeforeValidation()
    {
        var avatarId = Guid.NewGuid();
        var active = Stored(Guid.NewGuid(), avatarId, KycStatus.PENDING);
        active.ProviderResult = KycApprovalTrust.CreateEnvelope(new KycApprovalProfile(
            KycProvider.MANUAL,
            "manual",
            "retired-policy",
            "development-manual"));
        _store.Setup(store => store.GetActiveSubmissionByAvatarAsync(
                avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(active));

        var result = await _manager.SubmitAsync(ValidSubmission(), avatarId);

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
        var avatarId = Guid.NewGuid();
        _provider.Setup(p => p.ValidateDocumentsAsync(It.IsAny<IReadOnlyList<SubmitKycDocumentModel>>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<bool> { IsError = true, Message = "bad doc" });

        var result = await _manager.SubmitAsync(ValidSubmission(), avatarId);

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
    public async Task Submit_IgnoresCallerSuppliedAvatarId()
    {
        var authenticated = Guid.NewGuid();
        var forged = Guid.NewGuid();
        var model = ValidSubmission();
        model.AvatarId = forged;

        var result = await _manager.SubmitAsync(model, authenticated);

        result.Result!.AvatarId.Should().Be(authenticated);
    }

    // ── GetStatus ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatus_ReturnsLatest()
    {
        var avatarId = Guid.NewGuid();
        _store.Setup(s => s.GetLatestSubmissionByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = Stored(Guid.NewGuid(), avatarId, KycStatus.APPROVED) });

        var result = await _manager.GetStatusAsync(avatarId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(KycStatus.APPROVED);
    }

    [Fact]
    public async Task GetStatus_ApprovalFromRetiredPolicyProjectsExpired()
    {
        var avatarId = Guid.NewGuid();
        var approval = Stored(Guid.NewGuid(), avatarId, KycStatus.APPROVED);
        approval.ProviderResult = KycApprovalTrust.CreateEnvelope(new KycApprovalProfile(
            KycProvider.MANUAL,
            "manual",
            "retired-policy",
            "development-manual"));
        _store.Setup(store => store.GetLatestSubmissionByAvatarAsync(
                avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(approval));

        var result = await _manager.GetStatusAsync(avatarId);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(KycStatus.EXPIRED);
    }

    [Fact]
    public async Task GetStatus_None_ReturnsNotFound()
    {
        var avatarId = Guid.NewGuid();
        _store.Setup(s => s.GetLatestSubmissionByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });

        var result = await _manager.GetStatusAsync(avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(KycAuthorizationError.NotFound);
    }

    // ── Approve / Reject ──────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_ConditionallyCommitsLedgerDecision()
    {
        var avatarId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        var stored = Stored(submissionId, avatarId, KycStatus.PENDING);
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
    public async Task Reject_SetsRejectedWithReason()
    {
        var avatarId = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = Stored(submissionId, avatarId, KycStatus.PENDING) });

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

    // ── IDOR ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_DifferentAvatar_ReturnsForbidden()
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
    public async Task ListDocuments_DifferentAvatar_ReturnsForbidden()
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
