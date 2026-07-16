namespace AZOA.WebAPI.Models.Responses;

public sealed class NodeGovernanceParametersResponse
{
    public IReadOnlyList<string>? AllowedChains { get; set; }

    public IReadOnlyList<string>? AllowedAssetTypes { get; set; }

    public long Version { get; set; }

    public Guid? UpdatedByAvatarId { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class NodeGovernanceAuditResponse
{
    public Guid Id { get; set; }

    public string Action { get; set; } = string.Empty;

    public Guid ActorAvatarId { get; set; }

    public long PreviousVersion { get; set; }

    public long NewVersion { get; set; }

    public IReadOnlyList<string>? PreviousAllowedChains { get; set; }

    public IReadOnlyList<string>? PreviousAllowedAssetTypes { get; set; }

    public IReadOnlyList<string>? AllowedChains { get; set; }

    public IReadOnlyList<string>? AllowedAssetTypes { get; set; }

    public string? Detail { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
