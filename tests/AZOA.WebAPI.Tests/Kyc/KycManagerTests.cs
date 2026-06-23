using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Tests.Kyc;

public class KycManagerTests
{
    private readonly Mock<IKycStore> _store = new();
    private readonly Mock<IKycProviderService> _provider = new();
    private readonly Mock<IAvatarStore> _avatarStore = new();
    private readonly KycManager _manager;

    public KycManagerTests()
    {
        _manager = new KycManager(_store.Object, _provider.Object, _avatarStore.Object);

        // Defaults: no active submission, validation passes, session created, upsert echoes.
        _store.Setup(s => s.GetActiveSubmissionByAvatarAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });
        _store.Setup(s => s.UpsertSubmissionAsync(It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((KycSubmission sub, CancellationToken _) => new AZOAResult<KycSubmission> { Result = sub });
        _store.Setup(s => s.AddDocumentsAsync(It.IsAny<IEnumerable<KycDocument>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<bool> { Result = true });
        _store.Setup(s => s.GetDocumentsBySubmissionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<IEnumerable<KycDocument>> { Result = new List<KycDocument>() });
        _provider.Setup(p => p.ValidateDocumentsAsync(It.IsAny<IReadOnlyList<SubmitKycDocumentModel>>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<bool> { Result = true });
        _provider.Setup(p => p.CreateSessionAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<KycDocumentModel>>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((Guid id, IReadOnlyList<KycDocumentModel> _, CancellationToken __) => new AZOAResult<string> { Result = id.ToString("N") });
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
        Status = status,
        SubmittedAt = DateTimeOffset.UtcNow,
        CreatedDate = DateTimeOffset.UtcNow
    };

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
        _store.Verify(s => s.AddDocumentsAsync(It.IsAny<IEnumerable<KycDocument>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Submit_ActiveSubmissionExists_Rejected()
    {
        var avatarId = Guid.NewGuid();
        _store.Setup(s => s.GetActiveSubmissionByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = Stored(Guid.NewGuid(), avatarId, KycStatus.PENDING) });

        var result = await _manager.SubmitAsync(ValidSubmission(), avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("active KYC submission already exists");
        _store.Verify(s => s.UpsertSubmissionAsync(It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
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
    public async Task Approve_FlipsStatusAndAvatarIsVerified()
    {
        var avatarId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        var stored = Stored(submissionId, avatarId, KycStatus.PENDING);
        _store.Setup(s => s.GetSubmissionByIdAsync(submissionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<KycSubmission> { Result = stored });

        var avatar = new AZOA.WebAPI.Models.Avatar { Id = avatarId, IsVerified = false };
        _avatarStore.Setup(a => a.GetByIdAsync(avatarId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AZOAResult<IAvatar> { Result = avatar });
        _avatarStore.Setup(a => a.UpsertAsync(It.IsAny<IAvatar>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((IAvatar a, CancellationToken _) => new AZOAResult<IAvatar> { Result = a });

        var result = await _manager.ApproveAsync(submissionId, reviewerId, "looks good");

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(KycStatus.APPROVED);
        avatar.IsVerified.Should().BeTrue();
        _avatarStore.Verify(a => a.UpsertAsync(It.Is<IAvatar>(x => x.IsVerified), It.IsAny<CancellationToken>()), Times.Once);
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
        _avatarStore.Verify(a => a.UpsertAsync(It.IsAny<IAvatar>(), It.IsAny<CancellationToken>()), Times.Never);
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
