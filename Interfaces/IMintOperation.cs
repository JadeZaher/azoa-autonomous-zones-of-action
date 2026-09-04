using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Interfaces;

public interface IMintOperation : IBlockchainOperation
{
    string? TokenUri { get; set; }

    /// <summary>
    /// The quantity to mint, in the asset's base units (D13). Widened from <c>ulong</c>
    /// together with the operation record and the <c>operation_log.amount</c> column, so
    /// the ceiling moved off the type rather than one layer down.
    /// See <c>Core/AGENTS.md</c> §rail-amount.
    /// </summary>
    RailAmount Amount { get; set; }

    string? AssetType { get; set; }
}
