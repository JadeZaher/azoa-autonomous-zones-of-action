using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>KYC states exposed to tenant integrations.</summary>
public enum TenantKycStatus
{
    Unknown,
    Pending,
    Approved,
    Rejected,

    /// <summary>Identity is verified; a compliance check still awaits a human decision.</summary>
    /// <remarks>Appended last so the existing wire ordinals are unchanged.</remarks>
    UnderReview
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
    public string? UserId { get; set; }
    public string? WalletId { get; set; }
    public string? WalletAddress { get; set; }
    public TenantKycStatus KycStatus { get; set; }
    public bool IdentityReady { get; set; }
    public bool KycReady { get; set; }
    public bool WalletReady { get; set; }
    public bool Ready { get; set; }
    public string? UnavailableReason { get; set; }

    /// <summary>When an open manual review is due a decision; null unless UnderReview.</summary>
    public DateTimeOffset? ReviewDueAt { get; set; }

    /// <summary>Operator-set route to a human while UnderReview; null when unset.</summary>
    public string? ReviewContact { get; set; }

    /// <summary><see cref="TenantMemberOwnership"/> value; null only when the node could not resolve the member at all.</summary>
    public string? Ownership { get; set; }

    /// <summary><see cref="TenantMemberConsent"/> value for the grant this read was authorized under.</summary>
    public string? Consent { get; set; }

    /// <summary>Scopes of the live grant this read was authorized under (implied for custodial, the person's own once claimed).</summary>
    public IReadOnlyList<string> ConsentScopes { get; set; } = [];

    /// <summary>Expiry of that grant; null when it does not expire.</summary>
    public DateTimeOffset? ConsentExpiresAt { get; set; }
}

/// <summary>Wire values of <see cref="TenantCustodialAccountStatusResponse.Ownership"/>.</summary>
public static class TenantMemberOwnership
{
    public const string Custodial = "custodial";
    public const string Claimed = "claimed";
}

/// <summary>Wire values of <see cref="TenantCustodialAccountStatusResponse.Consent"/>.</summary>
public static class TenantMemberConsent
{
    public const string Granted = "granted";
    public const string None = "none";
}

/// <summary>Everything a tenant may learn about a claimed member whose grant does not cover kyc:read; see docs/TENANT-CUSTODIAL-ONBOARDING.md §Claimed members.</summary>
public sealed class TenantClaimedMemberStatusResponse
{
    public string Ownership { get; init; } = TenantMemberOwnership.Claimed;
    public string Consent { get; init; } = TenantMemberConsent.None;

    /// <summary>The person's first live grant's scopes; omitted when <see cref="Consent"/> is none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ConsentScopes { get; init; }

    /// <summary>That grant's expiry; omitted when there is no grant or it does not expire.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ConsentExpiresAt { get; init; }

    public TenantKycStatus KycStatus { get; init; } = TenantKycStatus.Unknown;
    public bool Ready { get; init; }
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
