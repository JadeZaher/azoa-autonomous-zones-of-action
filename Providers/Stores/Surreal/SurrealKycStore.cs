// SPDX-License-Identifier: UNLICENSED

using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IKycStore"/>.
///
/// ID encoding: <c>Guid.ToString("N").ToLowerInvariant()</c> (32-char hex, no
/// dashes). The <c>avatar_id</c> / <c>submission_id</c> foreign keys are
/// SurrealDB record links written via <see cref="SurrealLink"/>
/// (<c>table:id</c>) so SurrealDB 3.x's strict <c>record&lt;table&gt;</c>
/// coercion accepts them; reads strip the prefix back to bare hex.
///
/// The store round-trips the source-generated <see cref="KycSubmission"/> /
/// <see cref="KycDocument"/> POCOs directly: a private <c>ToStorage</c> swaps
/// the in-memory bare-hex id fields for link form before write, and
/// <c>FromStorage</c> reverses it after read. Mirrors
/// <c>SurrealStarStore</c>'s ToPoco/FromPoco pattern.
/// </summary>
public sealed class SurrealKycStore : IKycStore
{
    private readonly ISurrealExecutor _executor;

    public SurrealKycStore(ISurrealExecutor executor)
    {
        _executor = executor;
    }

    // ── Submissions ─────────────────────────────────────────────────────────

    public async Task<AZOAResult<KycSubmission>> GetSubmissionByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectById(KycSubmission.SchemaNameConst, SurrealId.ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<KycSubmission>(q, ct);
            return new AZOAResult<KycSubmission>
            {
                Message = row == null ? "No KYC submission found." : "Success",
                Result  = row == null ? null : FromStorage(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<KycSubmission>().CaptureException(ex, $"SurrealKycStore.GetSubmissionByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<KycSubmission>> GetLatestSubmissionByAvatarAsync(Guid avatarId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE avatar_id = $_avatar ORDER BY submitted_at DESC LIMIT 1")
                .WithParam("_t",      KycSubmission.SchemaNameConst)
                .WithParam("_avatar", SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(avatarId)));

            var row = await _executor.QuerySingleAsync<KycSubmission>(q, ct);
            return new AZOAResult<KycSubmission>
            {
                Message = row == null ? "No KYC submission found for avatar." : "Success",
                Result  = row == null ? null : FromStorage(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<KycSubmission>().CaptureException(ex, $"SurrealKycStore.GetLatestSubmissionByAvatarAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<KycSubmission>> GetActiveSubmissionByAvatarAsync(Guid avatarId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE avatar_id = $_avatar AND status INSIDE $_active ORDER BY submitted_at DESC LIMIT 1")
                .WithParam("_t",      KycSubmission.SchemaNameConst)
                .WithParam("_avatar", SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(avatarId)))
                .WithParam("_active", new[] { nameof(KycStatus.PENDING), nameof(KycStatus.IN_REVIEW) });

            var row = await _executor.QuerySingleAsync<KycSubmission>(q, ct);
            return new AZOAResult<KycSubmission>
            {
                Message = row == null ? "No active KYC submission." : "Success",
                Result  = row == null ? null : FromStorage(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<KycSubmission>().CaptureException(ex, $"SurrealKycStore.GetActiveSubmissionByAvatarAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<KycSubmission>>> GetPendingAsync(CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE status INSIDE $_active ORDER BY submitted_at ASC")
                .WithParam("_t",      KycSubmission.SchemaNameConst)
                .WithParam("_active", new[] { nameof(KycStatus.PENDING), nameof(KycStatus.IN_REVIEW) });

            var rows = await _executor.QueryAsync<KycSubmission>(q, ct);
            return new AZOAResult<IEnumerable<KycSubmission>>
            {
                Result  = rows.Select(FromStorage).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<KycSubmission>>().CaptureException(ex, $"SurrealKycStore.GetPendingAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<KycSubmission>> UpsertSubmissionAsync(KycSubmission submission, CancellationToken ct = default)
    {
        try
        {
            var body = ToStorage(submission);
            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    KycSubmission.SchemaNameConst)
                .WithParam("_id",   body.Id)
                .WithParam("_body", body);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved  = resp.GetValues<KycSubmission>(0).FirstOrDefault();
            var result = saved is not null ? FromStorage(saved) : submission;

            return new AZOAResult<KycSubmission> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<KycSubmission>().CaptureException(ex, $"SurrealKycStore.UpsertSubmissionAsync failed: {ex.Message}");
        }
    }

    // ── Documents ───────────────────────────────────────────────────────────

    public async Task<AZOAResult<IEnumerable<KycDocument>>> GetDocumentsBySubmissionAsync(Guid submissionId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE submission_id = $_submission ORDER BY created_date ASC")
                .WithParam("_t",          KycDocument.SchemaNameConst)
                .WithParam("_submission", SurrealLink.ToLink(KycSubmission.SchemaNameConst, SurrealId.ToSurrealId(submissionId)));

            var rows = await _executor.QueryAsync<KycDocument>(q, ct);
            return new AZOAResult<IEnumerable<KycDocument>>
            {
                Result  = rows.Select(FromStorage).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<KycDocument>>().CaptureException(ex, $"SurrealKycStore.GetDocumentsBySubmissionAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> AddDocumentsAsync(IEnumerable<KycDocument> documents, CancellationToken ct = default)
    {
        try
        {
            foreach (var doc in documents)
            {
                var body = ToStorage(doc);
                var q = SurrealQuery
                    .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                    .WithParam("_t",    KycDocument.SchemaNameConst)
                    .WithParam("_id",   body.Id)
                    .WithParam("_body", body);

                var resp = await _executor.ExecuteAsync(q, ct);
                resp.EnsureAllOk();
            }

            return new AZOAResult<bool> { Result = true, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealKycStore.AddDocumentsAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────


    /// <summary>
    /// Produce a write-ready clone whose FK fields carry the SurrealDB record-link
    /// form (<c>table:id</c>) and whose dates are UTC-kinded. The in-memory POCO
    /// keeps bare-hex id fields; this is the only place link encoding is applied.
    /// </summary>
    private static KycSubmission ToStorage(KycSubmission s) => new()
    {
        Id                = s.Id,
        AvatarId          = SurrealLink.ToLink("avatar", s.AvatarId),
        Provider          = s.Provider,
        Status            = s.Status,
        ReviewerId        = s.ReviewerId,
        ReviewNotes       = s.ReviewNotes,
        RejectionReason   = s.RejectionReason,
        ProviderSessionId = s.ProviderSessionId,
        ProviderResult    = s.ProviderResult,
        SubmittedAt       = AsUtc(s.SubmittedAt),
        ReviewedAt        = AsUtc(s.ReviewedAt),
        ExpiresAt         = AsUtc(s.ExpiresAt),
        CreatedDate       = AsUtc(s.CreatedDate),
        ModifiedDate      = AsUtc(s.ModifiedDate)
    };

    private static KycSubmission FromStorage(KycSubmission s) => new()
    {
        Id                = s.Id,
        AvatarId          = SurrealLink.FromLink(s.AvatarId),
        Provider          = s.Provider,
        Status            = s.Status,
        ReviewerId        = s.ReviewerId,
        ReviewNotes       = s.ReviewNotes,
        RejectionReason   = s.RejectionReason,
        ProviderSessionId = s.ProviderSessionId,
        ProviderResult    = s.ProviderResult,
        SubmittedAt       = s.SubmittedAt,
        ReviewedAt        = s.ReviewedAt,
        ExpiresAt         = s.ExpiresAt,
        CreatedDate       = s.CreatedDate,
        ModifiedDate      = s.ModifiedDate
    };

    private static KycDocument ToStorage(KycDocument d) => new()
    {
        Id            = d.Id,
        SubmissionId  = SurrealLink.ToLink(KycSubmission.SchemaNameConst, d.SubmissionId),
        Type          = d.Type,
        FileUrl       = d.FileUrl,
        FileName      = d.FileName,
        MimeType      = d.MimeType,
        FileSizeBytes = d.FileSizeBytes,
        Metadata      = d.Metadata,
        CreatedDate   = AsUtc(d.CreatedDate)
    };

    private static KycDocument FromStorage(KycDocument d) => new()
    {
        Id            = d.Id,
        SubmissionId  = SurrealLink.FromLink(d.SubmissionId),
        Type          = d.Type,
        FileUrl       = d.FileUrl,
        FileName      = d.FileName,
        MimeType      = d.MimeType,
        FileSizeBytes = d.FileSizeBytes,
        Metadata      = d.Metadata,
        CreatedDate   = d.CreatedDate
    };

    private static DateTimeOffset AsUtc(DateTimeOffset value)
        => value.ToUniversalTime();

    private static DateTimeOffset? AsUtc(DateTimeOffset? value)
        => value?.ToUniversalTime();
}
