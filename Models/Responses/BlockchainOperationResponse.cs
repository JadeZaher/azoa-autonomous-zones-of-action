// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Safe public projection of a durable operation. Internal parameter bags and
/// initiator/idempotency metadata never cross the authenticated read boundary.
/// </summary>
public sealed class BlockchainOperationResponse
{
    public Guid Id { get; init; }
    public Guid? AvatarId { get; init; }
    public Guid? WalletId { get; init; }
    public string OperationType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedDate { get; init; }
    public DateTime? CompletedDate { get; init; }

    /// <summary>Provider-neutral chain identifier, when the operation recorded one.</summary>
    public string? ChainType { get; init; }

    /// <summary>Provider-neutral network identifier, when the operation recorded one.</summary>
    public string? ChainNetwork { get; init; }

    /// <summary>Public transaction reference, when the provider recorded one.</summary>
    public string? TransactionReference { get; init; }

    /// <summary>Creates a strict allowlist projection for an authenticated caller.</summary>
    public static BlockchainOperationResponse From(IBlockchainOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var parameters = operation.Parameters ?? new Dictionary<string, string>();

        return new BlockchainOperationResponse
        {
            Id = operation.Id,
            AvatarId = operation.AvatarId,
            WalletId = operation.WalletId,
            OperationType = operation.OperationType,
            Status = operation.Status,
            CreatedDate = operation.CreatedDate,
            CompletedDate = operation.CompletedDate,
            ChainType = parameters.GetValueOrDefault("ChainType"),
            ChainNetwork = parameters.GetValueOrDefault("ChainNetwork"),
            TransactionReference = parameters.GetValueOrDefault("TxHash"),
        };
    }

    /// <summary>
    /// Rewraps an internal operation result for an HTTP boundary without carrying
    /// its parameter bag, provenance metadata, or captured exception forward.
    /// </summary>
    public static AZOAResult<BlockchainOperationResponse> Project(
        AZOAResult<IBlockchainOperation> operationResult)
    {
        ArgumentNullException.ThrowIfNull(operationResult);

        if (operationResult.IsError)
        {
            return new AZOAResult<BlockchainOperationResponse>
            {
                IsError = true,
                Message = operationResult.Message,
                Code = operationResult.Code,
                RetryAfterSeconds = operationResult.RetryAfterSeconds,
            };
        }

        if (operationResult.Result is null)
        {
            return AZOAResult<BlockchainOperationResponse>.FailureWithCode(
                "Operation outcome is unavailable.", AzoaErrorCodes.DependencyUnavailable);
        }

        return AZOAResult<BlockchainOperationResponse>.Success(
            From(operationResult.Result), operationResult.Message);
    }
}
