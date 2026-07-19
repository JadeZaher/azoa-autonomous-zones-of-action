using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>KYC states exposed to tenant integrations.</summary>
public enum TenantKycStatus
{
    Unknown,
    Pending,
    Approved,
    Rejected
}

/// <summary>Secret-free tenant account projection consumed by ArdaNova.</summary>
public sealed class TenantCustodialAccountStatusResponse
{
    public string TenantId { get; set; } = string.Empty;
    public string ExternalSubject { get; set; } = string.Empty;

    /// <summary>Compatibility alias for the first ArdaNova integration.</summary>
    [JsonPropertyName("ardanovaUserId")]
    public string ArdanovaUserId
    {
        get => ExternalSubject;
        set => ExternalSubject = value;
    }
    public string? AvatarId { get; set; }
    public string? WalletId { get; set; }
    public string? WalletAddress { get; set; }
    public TenantKycStatus KycStatus { get; set; }
    public bool IdentityReady { get; set; }
    public bool KycReady { get; set; }
    public bool WalletReady { get; set; }
    public bool Ready { get; set; }
    public string? UnavailableReason { get; set; }
}

/// <summary>Runtime capability report for tenant-managed custody and verification.</summary>
public sealed class TenantCustodialCapabilitiesResponse
{
    public bool Enabled { get; set; }
    public string WalletChain { get; set; } = string.Empty;
    public string CustodyMode { get; set; } = string.Empty;
    public bool CustodyAvailable { get; set; }
    public bool BlockchainProviderAvailable { get; set; }
    public string KycProvider { get; set; } = string.Empty;
    public bool KycAvailable { get; set; }
    public bool HostedVerification { get; set; }
    public bool AcceptsDocumentReferences { get; set; }
    public bool DevelopmentSimulation { get; set; }
    public bool IdentityReady { get; set; }
    public bool KycReady { get; set; }
    public bool WalletProvisioningReady { get; set; }
    public bool Ready { get; set; }
    public string? UnavailableReason { get; set; }
}

/// <summary>Safe KYC begin response; never contains documents or provider credentials.</summary>
public sealed class TenantKycSessionResponse
{
    public string Provider { get; set; } = string.Empty;
    public bool HostedVerification { get; set; }
    public bool AcceptsDocumentReferences { get; set; }
    public bool DevelopmentSimulation { get; set; }
    public string? VerificationUrl { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? Instructions { get; set; }
}

/// <summary>Safe KYC submission projection; source document references stay inside Azoa.</summary>
public sealed class TenantKycSubmissionResponse
{
    public string SubmissionId { get; set; } = string.Empty;
    public TenantKycStatus Status { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}
