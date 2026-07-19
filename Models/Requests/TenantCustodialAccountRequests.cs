using AZOA.WebAPI.Models.Kyc;
using System.ComponentModel.DataAnnotations;

namespace AZOA.WebAPI.Models.Requests;

/// <summary>One opaque HTTPS document reference for an Azoa-owned KYC submission.</summary>
public sealed class TenantKycDocumentReferenceRequest
{
    public KycDocumentType Type { get; set; }
    public string ReferenceUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public long? FileSizeBytes { get; set; }
}

/// <summary>Tenant request to submit document references for its correlated avatar.</summary>
public sealed class TenantKycSubmissionRequest
{
    [MaxLength(KycDocumentRequestLimits.MaxDocuments)]
    public List<TenantKycDocumentReferenceRequest> Documents { get; set; } = new();
}
