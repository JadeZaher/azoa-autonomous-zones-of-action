namespace AZOA.WebAPI.Models.Actions;

/// <summary>Metadata keys stored on <see cref="ActionDefinition.Metadata"/> for operator publish approval.</summary>
public static class PublishApprovalMetadataKeys
{
    public const string Status = "publishApprovalStatus";
    public const string VersionHash = "publishApprovalVersionHash";
    public const string RequestedAt = "publishApprovalRequestedAt";
    public const string RequestedBy = "publishApprovalRequestedBy";
    public const string DecidedAt = "publishApprovalDecidedAt";
    public const string DecidedBy = "publishApprovalDecidedBy";
    public const string Reason = "publishApprovalReason";
}

/// <summary>Lifecycle values recorded under <see cref="PublishApprovalMetadataKeys.Status"/>.</summary>
public static class ActionPublishApprovalStatuses
{
    public const string PendingApproval = "PendingApproval";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

/// <summary>Current node operator workflow publish approval policy state.</summary>
public sealed record ActionPublishApprovalPolicyView(
    bool RequireOperatorApprovalForPublish,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

/// <summary>Operator update payload for workflow publish approval policy.</summary>
public sealed class ActionPublishApprovalPolicyUpdateRequest
{
    public bool RequireOperatorApprovalForPublish { get; set; }
}

/// <summary>Operator approve/reject decision payload for a pending workflow publish request.</summary>
public sealed class ActionPublishApprovalDecisionRequest
{
    public bool Approve { get; set; }
    public string? Reason { get; set; }
}

/// <summary>Summary view of a workflow action participating in operator publish approval review.</summary>
public sealed record ActionPublishApprovalSummary(
    Guid ActionId,
    string Name,
    string? Description,
    Guid UserId,
    bool IsPublic,
    string Status,
    string ApprovalStatus,
    string? VersionHash,
    int NodeCount,
    int EdgeCount,
    string? RequestedAt,
    string? RequestedBy,
    string? DecidedAt,
    string? DecidedBy,
    string? Reason);
