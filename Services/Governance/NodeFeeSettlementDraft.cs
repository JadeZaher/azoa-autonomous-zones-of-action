using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>Immutable inputs required to freeze a future two-effect node fee settlement.</summary>
public sealed class NodeFeeSettlementDraft
{
    public string ParentIdempotencyKey { get; init; } = string.Empty;

    public NodeFeeOperation Operation { get; init; }

    public string Chain { get; init; } = string.Empty;

    public ChainNetwork Network { get; init; }

    public string AssetId { get; init; } = string.Empty;

    public string GrossAmount { get; init; } = string.Empty;

    public string FeeAmount { get; init; } = string.Empty;

    public string NetAmount { get; init; } = string.Empty;

    public long FeeScheduleVersion { get; init; }

    public string TreasuryAddress { get; init; } = string.Empty;

    public long TreasuryDestinationVersion { get; init; }
}
