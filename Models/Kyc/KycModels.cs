// SPDX-License-Identifier: UNLICENSED
// AZOA-side KYC DTOs. Ownership is keyed to Avatar (Guid) throughout — the
// authenticated avatar is authoritative; any AvatarId on a request body is
// ignored by the manager (IDOR defence-in-depth, the STARODK precedent).

#nullable enable

using System;
using System.Collections.Generic;

namespace AZOA.WebAPI.Models.Kyc;

/// <summary>
/// A single document supplied as part of a KYC submission request. Validated
/// by the active <c>IKycProviderService.ValidateDocumentsAsync</c> before any
/// row is written.
/// </summary>
public sealed class SubmitKycDocumentModel
{
    public KycDocumentType Type { get; set; }

    /// <summary>External blob URL of the uploaded document (storage out of scope).</summary>
    public string FileUrl { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>MIME type — validated against the provider allow-list when present.</summary>
    public string? MimeType { get; set; }

    /// <summary>Declared size in bytes — validated against the provider cap when present.</summary>
    public long? FileSizeBytes { get; set; }

    public string? Metadata { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/kyc/submit</c>. <see cref="AvatarId"/> is
/// accepted for shape-compatibility but IGNORED by the manager — the
/// authenticated avatar is always used.
/// </summary>
public sealed class SubmitKycModel
{
    /// <summary>IGNORED by the manager. The authenticated avatar is authoritative.</summary>
    public Guid? AvatarId { get; set; }

    public List<SubmitKycDocumentModel> Documents { get; set; } = new();
}

/// <summary>A persisted KYC document, projected for read responses + the provider seam.</summary>
public sealed class KycDocumentModel
{
    public Guid Id { get; set; }
    public Guid SubmissionId { get; set; }
    public KycDocumentType Type { get; set; }
    public string FileUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? Metadata { get; set; }
    public DateTime CreatedDate { get; set; }
}

/// <summary>A KYC submission projected for read responses.</summary>
public sealed class KycSubmissionModel
{
    public Guid Id { get; set; }
    public Guid AvatarId { get; set; }
    public KycProvider Provider { get; set; }
    public KycStatus Status { get; set; }
    public string? ReviewerId { get; set; }
    public string? ReviewNotes { get; set; }
    public string? RejectionReason { get; set; }
    public string? ProviderSessionId { get; set; }
    public string? ProviderResult { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public List<KycDocumentModel> Documents { get; set; } = new();
}
