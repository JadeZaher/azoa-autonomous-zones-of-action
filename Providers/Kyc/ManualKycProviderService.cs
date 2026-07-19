// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Providers.Kyc;

/// <summary>
/// Default KYC provider: in-house manual admin review. No external session is
/// created — the submission status is driven entirely by the admin
/// approve/reject endpoints. Requires no provider secrets.
/// </summary>
public sealed class ManualKycProviderService : IKycProviderService
{
    private readonly KycSettings _settings;

    public ManualKycProviderService(IOptions<KycSettings>? settings = null)
    {
        _settings = settings?.Value ?? new KycSettings();
    }

    public KycProvider Provider => KycProvider.MANUAL;
    public string ProviderKey => "manual";

    public KycProviderCapabilitiesModel GetCapabilities() => new()
    {
        Provider = Provider,
        ProviderKey = ProviderKey,
        Available = true,
        HostedVerification = false,
        AcceptsDocumentReferences = true
    };

    public Task<AZOAResult<KycSessionStartModel>> BeginSessionAsync(Guid avatarId, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<KycSessionStartModel>.Success(new KycSessionStartModel
        {
            Provider = Provider,
            ProviderKey = ProviderKey,
            HostedVerification = false,
            AcceptsDocumentReferences = true,
            ExpiresAt = DateTime.UtcNow.AddMinutes(Math.Clamp(_settings.SessionExpiryMinutes, 1, 1440)),
            Instructions = "Upload identity documents to approved HTTPS storage, then submit only their references."
        }));

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
            if (doc is null)
                return Fail("Document references must not contain null entries.");

            if (string.IsNullOrWhiteSpace(doc.FileUrl))
                return Fail($"Document '{doc.FileName}' is missing a file URL.");

            if (string.IsNullOrWhiteSpace(doc.FileName))
                return Fail("Document file name is required.");

            if (doc.FileName.Length > 255)
                return Fail("Document file name exceeds 255 characters.");

            if (doc.FileUrl.Length > 2048
                || !Uri.TryCreate(doc.FileUrl, UriKind.Absolute, out var reference)
                || !string.Equals(reference.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(reference.UserInfo)
                || !string.IsNullOrEmpty(reference.Query)
                || !string.IsNullOrEmpty(reference.Fragment)
                || reference.IsLoopback)
            {
                return Fail($"Document '{doc.FileName}' must use an absolute, credential-free HTTPS reference.");
            }

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
