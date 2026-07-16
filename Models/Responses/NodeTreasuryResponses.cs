using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Responses;

public sealed class NodeTreasuryDestinationResponse
{
    public string Chain { get; set; } = string.Empty;

    public ChainNetwork Network { get; set; }

    public string Address { get; set; } = string.Empty;

    public long Version { get; set; }

    public Guid? UpdatedByAvatarId { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class NodeTreasuryAuditResponse
{
    public Guid Id { get; set; }

    public string Action { get; set; } = string.Empty;

    public Guid ActorAvatarId { get; set; }

    public string Chain { get; set; } = string.Empty;

    public ChainNetwork Network { get; set; }

    public long PreviousVersion { get; set; }

    public long NewVersion { get; set; }

    public string? PreviousDestinationJson { get; set; }

    public string DestinationJson { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
