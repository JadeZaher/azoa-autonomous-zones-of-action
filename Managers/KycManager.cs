// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Provider-agnostic KYC manager. Avatar-scoped, returns
/// <see cref="AZOAResult{T}"/>, and flags authorisation failures with the
/// <see cref="KycAuthorizationError"/> message-prefix discriminator so the
/// controller can translate to 403/404. On approval the owning avatar's
/// <c>IsVerified</c> flips true; the gate reads submission status, so no
/// verification-level column is added to the shared avatar table.
/// </summary>
public sealed class KycManager : IKycManager
{
    private readonly IKycStore _store;
    private readonly IKycProviderService _provider;
    private readonly IAvatarStore _avatarStore;

    public KycManager(IKycStore store, IKycProviderService provider, IAvatarStore avatarStore)
    {
        _store = store;
        _provider = provider;
        _avatarStore = avatarStore;
    }

    public async Task<AZOAResult<KycSubmissionModel>> SubmitAsync(SubmitKycModel model, Guid avatarId, CancellationToken ct = default)
    {
        // Reject when an active submission already exists for this avatar.
        var active = await _store.GetActiveSubmissionByAvatarAsync(avatarId, ct);
        if (active.IsError)
            return Error<KycSubmissionModel>(active.Message, active.Exception);
        if (active.Result is not null)
            return Error<KycSubmissionModel>(
                "An active KYC submission already exists for this avatar. Please wait for the current submission to be reviewed.");

        // Validate documents via the active provider before any row is written.
        var validation = await _provider.ValidateDocumentsAsync(model.Documents, ct);
        if (validation.IsError)
            return Error<KycSubmissionModel>(validation.Message, validation.Exception);

        var now = DateTimeOffset.UtcNow;
        var submissionId = Guid.NewGuid();

        var submission = new KycSubmission
        {
            Id          = ToSurrealId(submissionId),
            AvatarId    = ToSurrealId(avatarId),
            Provider    = KycProvider.MANUAL,
            Status      = KycStatus.PENDING,
            SubmittedAt = now,
            CreatedDate = now,
            ModifiedDate = now
        };

        var savedSubmission = await _store.UpsertSubmissionAsync(submission, ct);
        if (savedSubmission.IsError || savedSubmission.Result is null)
            return Error<KycSubmissionModel>(savedSubmission.Message, savedSubmission.Exception);

        // Persist the documents.
        var documents = model.Documents.Select(d => new KycDocument
        {
            Id            = ToSurrealId(Guid.NewGuid()),
            SubmissionId  = ToSurrealId(submissionId),
            Type          = d.Type,
            FileUrl       = d.FileUrl,
            FileName      = d.FileName,
            MimeType      = d.MimeType,
            FileSizeBytes = d.FileSizeBytes,
            Metadata      = d.Metadata,
            CreatedDate   = now
        }).ToList();

        if (documents.Count > 0)
        {
            var savedDocs = await _store.AddDocumentsAsync(documents, ct);
            if (savedDocs.IsError)
                return Error<KycSubmissionModel>(savedDocs.Message, savedDocs.Exception);
        }

        // Open a provider session and stamp the session id onto the submission.
        var docModels = documents.Select(ToDocumentModel).ToList();
        var session = await _provider.CreateSessionAsync(avatarId, docModels, ct);
        if (!session.IsError && !string.IsNullOrEmpty(session.Result))
        {
            submission.ProviderSessionId = session.Result;
            submission.ModifiedDate = DateTimeOffset.UtcNow;
            var restamped = await _store.UpsertSubmissionAsync(submission, ct);
            if (restamped.IsError)
                return Error<KycSubmissionModel>(restamped.Message, restamped.Exception);
        }

        var result = ToSubmissionModel(submission);
        result.Documents = docModels;
        return Ok(result);
    }

    public async Task<AZOAResult<KycSubmissionModel>> GetStatusAsync(Guid avatarId, CancellationToken ct = default)
    {
        var latest = await _store.GetLatestSubmissionByAvatarAsync(avatarId, ct);
        if (latest.IsError)
            return Error<KycSubmissionModel>(latest.Message, latest.Exception);
        if (latest.Result is null)
            return Error<KycSubmissionModel>($"{KycAuthorizationError.NotFound}No KYC submission found for this avatar.");

        return await WithDocuments(latest.Result, ct);
    }

    public async Task<AZOAResult<KycSubmissionModel>> GetByIdAsync(Guid submissionId, Guid avatarId, CancellationToken ct = default)
    {
        var loaded = await LoadOwned(submissionId, avatarId, ct);
        if (loaded.IsError || loaded.Result is null)
            return Error<KycSubmissionModel>(loaded.Message, loaded.Exception);

        return await WithDocuments(loaded.Result, ct);
    }

    public async Task<AZOAResult<IEnumerable<KycDocumentModel>>> ListDocumentsAsync(Guid submissionId, Guid avatarId, CancellationToken ct = default)
    {
        var loaded = await LoadOwned(submissionId, avatarId, ct);
        if (loaded.IsError || loaded.Result is null)
            return Error<IEnumerable<KycDocumentModel>>(loaded.Message, loaded.Exception);

        var docs = await _store.GetDocumentsBySubmissionAsync(submissionId, ct);
        if (docs.IsError)
            return Error<IEnumerable<KycDocumentModel>>(docs.Message, docs.Exception);

        return Ok<IEnumerable<KycDocumentModel>>(
            (docs.Result ?? Enumerable.Empty<KycDocument>()).Select(ToDocumentModel).ToList());
    }

    // ── Admin surface ─────────────────────────────────────────────────────────

    public async Task<AZOAResult<IEnumerable<KycSubmissionModel>>> GetPendingAsync(CancellationToken ct = default)
    {
        var pending = await _store.GetPendingAsync(ct);
        if (pending.IsError)
            return Error<IEnumerable<KycSubmissionModel>>(pending.Message, pending.Exception);

        var models = new List<KycSubmissionModel>();
        foreach (var submission in pending.Result ?? Enumerable.Empty<KycSubmission>())
        {
            var withDocs = await WithDocuments(submission, ct);
            if (withDocs.IsError || withDocs.Result is null)
                return Error<IEnumerable<KycSubmissionModel>>(withDocs.Message, withDocs.Exception);
            models.Add(withDocs.Result);
        }

        return Ok<IEnumerable<KycSubmissionModel>>(models);
    }

    public async Task<AZOAResult<KycSubmissionModel>> ApproveAsync(Guid submissionId, Guid reviewerAvatarId, string? notes, CancellationToken ct = default)
    {
        var loaded = await _store.GetSubmissionByIdAsync(submissionId, ct);
        if (loaded.IsError)
            return Error<KycSubmissionModel>(loaded.Message, loaded.Exception);
        if (loaded.Result is null)
            return Error<KycSubmissionModel>($"{KycAuthorizationError.NotFound}KYC submission not found.");

        var submission = loaded.Result;
        if (submission.Status is not (KycStatus.PENDING or KycStatus.IN_REVIEW))
            return Error<KycSubmissionModel>(
                $"Cannot approve a submission with status {submission.Status}. Only PENDING or IN_REVIEW submissions can be approved.");

        var now = DateTimeOffset.UtcNow;
        submission.Status       = KycStatus.APPROVED;
        submission.ReviewerId   = ToSurrealId(reviewerAvatarId);
        submission.ReviewNotes  = notes;
        submission.ReviewedAt   = now;
        submission.ModifiedDate = now;

        var saved = await _store.UpsertSubmissionAsync(submission, ct);
        if (saved.IsError || saved.Result is null)
            return Error<KycSubmissionModel>(saved.Message, saved.Exception);

        // Flip the owning avatar's IsVerified flag (D3 — no VerificationLevel column).
        var flip = await SetAvatarVerified(submission.AvatarId, ct);
        if (flip.IsError)
            return Error<KycSubmissionModel>(flip.Message, flip.Exception);

        return await WithDocuments(saved.Result, ct);
    }

    public async Task<AZOAResult<KycSubmissionModel>> RejectAsync(Guid submissionId, Guid reviewerAvatarId, string? notes, string? rejectionReason, CancellationToken ct = default)
    {
        var loaded = await _store.GetSubmissionByIdAsync(submissionId, ct);
        if (loaded.IsError)
            return Error<KycSubmissionModel>(loaded.Message, loaded.Exception);
        if (loaded.Result is null)
            return Error<KycSubmissionModel>($"{KycAuthorizationError.NotFound}KYC submission not found.");

        var submission = loaded.Result;
        if (submission.Status is not (KycStatus.PENDING or KycStatus.IN_REVIEW))
            return Error<KycSubmissionModel>(
                $"Cannot reject a submission with status {submission.Status}. Only PENDING or IN_REVIEW submissions can be rejected.");

        var now = DateTimeOffset.UtcNow;
        submission.Status          = KycStatus.REJECTED;
        submission.ReviewerId      = ToSurrealId(reviewerAvatarId);
        submission.ReviewNotes     = notes;
        submission.RejectionReason = rejectionReason;
        submission.ReviewedAt      = now;
        submission.ModifiedDate    = now;

        var saved = await _store.UpsertSubmissionAsync(submission, ct);
        if (saved.IsError || saved.Result is null)
            return Error<KycSubmissionModel>(saved.Message, saved.Exception);

        return await WithDocuments(saved.Result, ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads a submission by id and asserts the authenticated avatar owns it.
    /// Returns <see cref="KycAuthorizationError.NotFound"/> when the id is unknown
    /// and <see cref="KycAuthorizationError.Forbidden"/> when it is owned by a
    /// different avatar (the IDOR hardening vs the unscoped source).
    /// </summary>
    private async Task<AZOAResult<KycSubmission>> LoadOwned(Guid submissionId, Guid avatarId, CancellationToken ct)
    {
        var loaded = await _store.GetSubmissionByIdAsync(submissionId, ct);
        if (loaded.IsError)
            return Error<KycSubmission>(loaded.Message, loaded.Exception);
        if (loaded.Result is null)
            return Error<KycSubmission>($"{KycAuthorizationError.NotFound}KYC submission not found.");

        if (!OwnedBy(loaded.Result, avatarId))
            return Error<KycSubmission>($"{KycAuthorizationError.Forbidden}{KycAuthorizationError.VerificationRequiredMessage}");

        return Ok(loaded.Result);
    }

    private static bool OwnedBy(KycSubmission submission, Guid avatarId)
        => Guid.TryParse(submission.AvatarId, out var owner)
           && owner == avatarId;

    private async Task<AZOAResult<KycSubmissionModel>> WithDocuments(KycSubmission submission, CancellationToken ct)
    {
        if (!Guid.TryParse(submission.Id, out var submissionId))
            return Error<KycSubmissionModel>("KYC submission has an unparseable id.");

        var docs = await _store.GetDocumentsBySubmissionAsync(submissionId, ct);
        if (docs.IsError)
            return Error<KycSubmissionModel>(docs.Message, docs.Exception);

        var model = ToSubmissionModel(submission);
        model.Documents = (docs.Result ?? Enumerable.Empty<KycDocument>()).Select(ToDocumentModel).ToList();
        return Ok(model);
    }

    private async Task<AZOAResult<bool>> SetAvatarVerified(string? avatarHexId, CancellationToken ct)
    {
        if (!Guid.TryParse(avatarHexId, out var avatarId))
            return Error<bool>("KYC submission has no owning avatar to verify.");

        var loaded = await _avatarStore.GetByIdAsync(avatarId, ct);
        if (loaded.IsError)
            return Error<bool>(loaded.Message, loaded.Exception);
        if (loaded.Result is null)
            return Error<bool>($"{KycAuthorizationError.NotFound}Owning avatar not found.");

        if (loaded.Result.IsVerified)
            return Ok(true);

        loaded.Result.IsVerified = true;
        var saved = await _avatarStore.UpsertAsync(loaded.Result, ct);
        if (saved.IsError)
            return Error<bool>(saved.Message, saved.Exception);

        return Ok(true);
    }

    private static string ToSurrealId(Guid id) => id.ToString("N").ToLowerInvariant();

    private static KycDocumentModel ToDocumentModel(KycDocument d) => new()
    {
        Id            = Guid.TryParse(d.Id, out var id) ? id : Guid.Empty,
        SubmissionId  = Guid.TryParse(d.SubmissionId, out var sid) ? sid : Guid.Empty,
        Type          = d.Type,
        FileUrl       = d.FileUrl,
        FileName      = d.FileName,
        MimeType      = d.MimeType,
        FileSizeBytes = d.FileSizeBytes,
        Metadata      = d.Metadata,
        CreatedDate   = d.CreatedDate.UtcDateTime
    };

    private static KycSubmissionModel ToSubmissionModel(KycSubmission s) => new()
    {
        Id                = Guid.TryParse(s.Id, out var id) ? id : Guid.Empty,
        AvatarId          = Guid.TryParse(s.AvatarId, out var aid) ? aid : Guid.Empty,
        Provider          = s.Provider,
        Status            = s.Status,
        ReviewerId        = s.ReviewerId,
        ReviewNotes       = s.ReviewNotes,
        RejectionReason   = s.RejectionReason,
        ProviderSessionId = s.ProviderSessionId,
        ProviderResult    = s.ProviderResult,
        SubmittedAt       = s.SubmittedAt.UtcDateTime,
        ReviewedAt        = s.ReviewedAt?.UtcDateTime,
        ExpiresAt         = s.ExpiresAt?.UtcDateTime,
        CreatedDate       = s.CreatedDate.UtcDateTime,
        ModifiedDate      = s.ModifiedDate?.UtcDateTime
    };

    private static AZOAResult<T> Ok<T>(T result) => new() { Result = result, Message = "Success" };

    private static AZOAResult<T> Error<T>(string message, Exception? ex = null)
    {
        var r = new AZOAResult<T> { IsError = true, Message = message };
        if (ex is not null) r.Exception = ex;
        return r;
    }
}
