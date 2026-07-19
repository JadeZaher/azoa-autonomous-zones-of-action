namespace AZOA.WebAPI.Models.Responses;

public static class NodeOperatorErrorCodes
{
    public const string LoginThrottled = "NODE_OPERATOR_LOGIN_THROTTLED";
    public const string ServiceUnavailable = "OPERATOR_SERVICE_UNAVAILABLE";
}

public sealed class NodeOperatorSessionResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public string Username { get; set; } = string.Empty;
}

public sealed class NodeOperatorOverviewResponse
{
    public NodeRuntimeSummary Node { get; set; } = new();
    public NodeOperatorIdentitySummary Operator { get; set; } = new();
    public KycControlSummary Kyc { get; set; } = new();
}

public sealed class NodeRuntimeSummary
{
    public string Environment { get; set; } = string.Empty;
    public string ServiceVersion { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
    public bool PersistenceReady { get; set; }
}

public sealed class NodeOperatorIdentitySummary
{
    public string Username { get; set; } = string.Empty;
    public long CredentialRevision { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset CredentialUpdatedAt { get; set; }
}

public sealed class KycControlSummary
{
    public int ProfileCount { get; set; }
    public int EnabledProfileCount { get; set; }
    public int ReadyProfileCount { get; set; }
    public int PendingSubmissionCount { get; set; }
    public int ConfiguredTenantCount { get; set; }
}

public sealed class KycProviderProfileResponse
{
    public string ProviderKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AdapterKey { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool Available { get; set; }
    public bool ApiKeyConfigured { get; set; }
    public bool WebhookSecretConfigured { get; set; }
    public string ReadinessCode { get; set; } = string.Empty;
    public IReadOnlyList<string> RequiredConfigurationKeys { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingConfigurationKeys { get; set; } = Array.Empty<string>();
    public string PolicyVersion { get; set; } = string.Empty;
    public string AssuranceLevel { get; set; } = string.Empty;
    public long Version { get; set; }
    public long TrustRevision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TenantKycProviderChoiceResponse
{
    public string ProviderKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AssuranceLevel { get; set; } = string.Empty;
    public bool HostedVerification { get; set; }
    public bool AcceptsDocumentReferences { get; set; }
}

public sealed class CursorPage<T>
{
    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
    public string? NextCursor { get; set; }
}

public class TenantKycSelectionResponse
{
    public string TenantId { get; set; } = string.Empty;
    public string? ProviderKey { get; set; }
    public string? ProviderDisplayName { get; set; }
    public long SelectionVersion { get; set; }
    public bool ProviderEnabled { get; set; }
    public bool ProviderAvailable { get; set; }
    public string ReadinessCode { get; set; } = string.Empty;
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class OperatorTenantKycSummaryResponse : TenantKycSelectionResponse
{
    public string Username { get; set; } = string.Empty;
}

public sealed class OperatorKycSubmissionQueueItem
{
    public Guid Id { get; set; }
    public Guid AvatarId { get; set; }
    public Guid? TenantId { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool HumanReviewAllowed { get; set; }
    public string ReviewMode { get; set; } = string.Empty;
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class KycControlAuditResponse
{
    public Guid Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid? TenantId { get; set; }
    public string? ProviderKey { get; set; }
    public string? PreviousProviderKey { get; set; }
    public long Version { get; set; }
    public string? PreviousDisplayName { get; set; }
    public string? DisplayName { get; set; }
    public string? PreviousAdapterKey { get; set; }
    public string? AdapterKey { get; set; }
    public bool? PreviousEnabled { get; set; }
    public bool? Enabled { get; set; }
    public string? PreviousPolicyVersion { get; set; }
    public string? PolicyVersion { get; set; }
    public string? PreviousAssuranceLevel { get; set; }
    public string? AssuranceLevel { get; set; }
    public long? PreviousTrustRevision { get; set; }
    public long? TrustRevision { get; set; }
    public Guid ActorAvatarId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
