using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Prepares value transfers for durable storage before any broadcast.</summary>
public interface IValueTransferTransactionModule : IBlockchainProviderModule
{
    /// <summary>Signs without submission; callers persist the returned bytes before sending.</summary>
    Task<AZOAResult<PreparedValueTransfer>> PrepareValueTransferAsync(ValueTransferIntent intent, CancellationToken ct);
    /// <summary>Submits the exact persisted transaction, preserving identity on every retry.</summary>
    Task SubmitValueTransferAsync(PreparedValueTransfer prepared, CancellationToken ct);
    /// <summary>Checks the original transaction on its bound network without rebuilding it.</summary>
    Task<AZOAResult<ChainConfirmation>> ObserveValueTransferAsync(PreparedValueTransfer prepared, CancellationToken ct);
}
