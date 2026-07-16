using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Responses;

public sealed class NodeTransparencySnapshotResponse
{
    public PublicNodeGovernanceParametersResponse Governance { get; set; } = new();

    public PublicNodeFeeScheduleResponse FeeSchedule { get; set; } = new();

    public IReadOnlyList<PublicNodeTreasuryDestinationResponse> TreasuryDestinations { get; set; } = [];

    public DateTimeOffset? LastUpdatedAt { get; set; }

    public string ContentSha256 { get; set; } = string.Empty;

    public bool CryptographicHistoryProofAvailable { get; set; }
}

public sealed class PublicNodeGovernanceParametersResponse
{
    public IReadOnlyList<string>? AllowedChains { get; set; }

    public IReadOnlyList<string>? AllowedAssetTypes { get; set; }

    public long Version { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class PublicNodeFeeScheduleResponse
{
    public NodeFeeScheduleEntryResponse Mint { get; set; } = new();

    public NodeFeeScheduleEntryResponse Transfer { get; set; } = new();

    public NodeFeeScheduleEntryResponse Swap { get; set; } = new();

    public NodeFeeScheduleEntryResponse QuestComplete { get; set; } = new();

    public NodeFeeScheduleEntryResponse FederationPublish { get; set; } = new();

    public long Version { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class PublicNodeTreasuryDestinationResponse
{
    public string Chain { get; set; } = string.Empty;

    public ChainNetwork Network { get; set; }

    public string Address { get; set; } = string.Empty;

    public long Version { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class NodeTransparencyPageResponse<T>
{
    public IReadOnlyList<T> Items { get; set; } = [];

    public string? NextCursor { get; set; }

    public string ContentSha256 { get; set; } = string.Empty;

    public bool CryptographicHistoryProofAvailable { get; set; }
}

public sealed class PublicNodeGovernanceAuditResponse
{
    public string Action { get; set; } = string.Empty;

    public long PreviousVersion { get; set; }

    public long NewVersion { get; set; }

    public IReadOnlyList<string>? PreviousAllowedChains { get; set; }

    public IReadOnlyList<string>? PreviousAllowedAssetTypes { get; set; }

    public IReadOnlyList<string>? AllowedChains { get; set; }

    public IReadOnlyList<string>? AllowedAssetTypes { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class PublicNodeFeeAuditResponse
{
    public string Action { get; set; } = string.Empty;

    public long PreviousVersion { get; set; }

    public long NewVersion { get; set; }

    public PublicNodeFeeScheduleResponse? PreviousSchedule { get; set; }

    public PublicNodeFeeScheduleResponse Schedule { get; set; } = new();

    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class PublicNodeTreasuryAuditResponse
{
    public string Action { get; set; } = string.Empty;

    public string Chain { get; set; } = string.Empty;

    public ChainNetwork Network { get; set; }

    public long PreviousVersion { get; set; }

    public long NewVersion { get; set; }

    public PublicNodeTreasuryDestinationResponse? PreviousDestination { get; set; }

    public PublicNodeTreasuryDestinationResponse Destination { get; set; } = new();

    public DateTimeOffset OccurredAt { get; set; }
}
