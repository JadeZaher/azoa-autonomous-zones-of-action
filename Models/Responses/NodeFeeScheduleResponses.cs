using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Models.Responses;

public sealed class NodeFeeScheduleResponse
{
    public NodeFeeScheduleEntryResponse Mint { get; set; } = new();

    public NodeFeeScheduleEntryResponse Transfer { get; set; } = new();

    public NodeFeeScheduleEntryResponse Swap { get; set; } = new();

    public NodeFeeScheduleEntryResponse QuestComplete { get; set; } = new();

    public NodeFeeScheduleEntryResponse FederationPublish { get; set; } = new();

    public long Version { get; set; }

    public Guid? UpdatedByAvatarId { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class NodeFeeScheduleEntryResponse
{
    public string FlatBaseUnits { get; set; } = "0";

    public long Bps { get; set; }
}

public sealed class NodeFeeAuditResponse
{
    public Guid Id { get; set; }

    public string Action { get; set; } = string.Empty;

    public Guid ActorAvatarId { get; set; }

    public long PreviousVersion { get; set; }

    public long NewVersion { get; set; }

    public string? PreviousScheduleJson { get; set; }

    public string ScheduleJson { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class NodeFeeQuoteResponse
{
    public NodeFeeOperation Operation { get; set; }

    public string GrossAmount { get; set; } = "0";

    public string FeeAmount { get; set; } = "0";

    public string NetAmount { get; set; } = "0";

    public string FlatBaseUnits { get; set; } = "0";

    public long Bps { get; set; }

    public long ScheduleVersion { get; set; }
}
