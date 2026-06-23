// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Kyc;

/// <summary>
/// Default KYC provider: in-house manual admin review. No external session is
/// created — the submission status is driven entirely by the admin
/// approve/reject endpoints. Requires no provider secrets.
/// </summary>
public sealed class ManualKycProviderService : IKycProviderService
{
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/gif",
        "image/webp",
        "image/bmp",
        "application/pdf"
    };

    private const int MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    public Task<AZOAResult<string>> CreateSessionAsync(Guid avatarId, IReadOnlyList<KycDocumentModel> documents, CancellationToken ct = default)
    {
        // The manual provider does not create an external session. The avatar id
        // is returned as a pseudo-session identifier; the real submission id is
        // owned by the manager.
        return Task.FromResult(new AZOAResult<string>
        {
            Result  = avatarId.ToString("N"),
            Message = "Manual provider — no external session."
        });
    }

    public Task<AZOAResult<KycStatus>> GetSessionStatusAsync(string providerSessionId, CancellationToken ct = default)
    {
        // No external session tracking; status is managed via the database +
        // admin review endpoints. PENDING until a reviewer decides.
        return Task.FromResult(new AZOAResult<KycStatus> { Result = KycStatus.PENDING, Message = "Success" });
    }

    public Task<AZOAResult<KycStatus>> HandleWebhookAsync(string payload, CancellationToken ct = default)
    {
        // No-op for the manual provider — the manual flow uses approve/reject.
        return Task.FromResult(new AZOAResult<KycStatus> { Result = KycStatus.PENDING, Message = "Success" });
    }

    public Task<AZOAResult<bool>> ValidateDocumentsAsync(IReadOnlyList<SubmitKycDocumentModel> documents, CancellationToken ct = default)
    {
        if (documents is null || documents.Count == 0)
            return Fail("At least one document is required.");

        foreach (var doc in documents)
        {
            if (string.IsNullOrWhiteSpace(doc.FileUrl))
                return Fail($"Document '{doc.FileName}' is missing a file URL.");

            if (string.IsNullOrWhiteSpace(doc.FileName))
                return Fail("Document file name is required.");

            if (!string.IsNullOrWhiteSpace(doc.MimeType) && !AllowedMimeTypes.Contains(doc.MimeType))
                return Fail($"Document '{doc.FileName}' has an unsupported file type '{doc.MimeType}'. Allowed types: JPEG, PNG, GIF, WebP, BMP, PDF.");

            if (doc.FileSizeBytes.HasValue && doc.FileSizeBytes.Value > MaxFileSizeBytes)
                return Fail($"Document '{doc.FileName}' exceeds the maximum file size of {MaxFileSizeBytes / (1024 * 1024)} MB.");

            if (doc.FileSizeBytes.HasValue && doc.FileSizeBytes.Value <= 0)
                return Fail($"Document '{doc.FileName}' has an invalid file size.");
        }

        return Task.FromResult(new AZOAResult<bool> { Result = true, Message = "Success" });
    }

    private static Task<AZOAResult<bool>> Fail(string message)
        => Task.FromResult(new AZOAResult<bool> { IsError = true, Result = false, Message = message });
}
