namespace AZOA.WebAPI.Models.Requests;

public sealed class NodeFeeScheduleUpdateRequest
{
    public long? ExpectedVersion { get; set; }

    public NodeFeeScheduleEntryRequest? Mint { get; set; }

    public NodeFeeScheduleEntryRequest? Transfer { get; set; }

    public NodeFeeScheduleEntryRequest? Swap { get; set; }

    public NodeFeeScheduleEntryRequest? QuestComplete { get; set; }

    public NodeFeeScheduleEntryRequest? FederationPublish { get; set; }
}

public sealed class NodeFeeScheduleEntryRequest
{
    public string? FlatBaseUnits { get; set; }

    public long? Bps { get; set; }
}
