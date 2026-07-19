// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Resolves caller-scoped allocation receipts and delegates only chain-truth
/// observation to reconciliation. See <see cref="IAllocationReceiptManager"/>.
/// </summary>
public sealed class AllocationReceiptManager : IAllocationReceiptManager
{
    private const string AllocationOperationType = "fiat_allocation";
    private const string AllocationFailedCode = "ALLOCATION_FAILED";

    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IBlockchainOperationStore _operationStore;
    private readonly IReconciliationService _reconciliation;

    public AllocationReceiptManager(
        IIdempotencyStore idempotencyStore,
        IBlockchainOperationStore operationStore,
        IReconciliationService reconciliation)
    {
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _operationStore = operationStore ?? throw new ArgumentNullException(nameof(operationStore));
        _reconciliation = reconciliation ?? throw new ArgumentNullException(nameof(reconciliation));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<AllocationReceiptResponse>> GetAsync(
        AllocationReceiptRequest request,
        CancellationToken ct = default)
    {
        if (!IsValid(request))
            return InvalidRequest();

        var resolved = await ResolveAsync(request, ct);
        return ProjectResolution(resolved, "Allocation receipt found.");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<AllocationReceiptResponse>> ReconcileAsync(
        AllocationReceiptRequest request,
        CancellationToken ct = default)
    {
        if (!IsValid(request))
            return InvalidRequest();

        var resolved = await ResolveAsync(request, ct);
        if (resolved.IsError || resolved.Result is null)
            return ProjectResolution(resolved, "Allocation reconciliation observation completed.");

        if (resolved.Result.Operation is null)
        {
            return AZOAResult<AllocationReceiptResponse>.Success(
                resolved.Result.Response,
                "Allocation receipt is awaiting a durable operation.");
        }

        if (!resolved.Result.Response.RequiresReconciliation)
        {
            return AZOAResult<AllocationReceiptResponse>.Success(
                resolved.Result.Response,
                "Allocation receipt is already terminal.");
        }

        try
        {
            var reconciliation = await _reconciliation.ReconcileOperationAsync(
                resolved.Result.Operation.Id,
                ct);
            if (reconciliation.Errors > 0)
                return DependencyUnavailable();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DependencyUnavailable();
        }

        var refreshed = await ResolveAsync(request, ct);
        return ProjectResolution(refreshed, "Allocation reconciliation observation completed.");
    }

    private async Task<AZOAResult<ReceiptResolution>> ResolveAsync(
        AllocationReceiptRequest request,
        CancellationToken ct)
    {
        var identity = AllocationIdempotency.CreateFromClientKey(
            request.ApiKeyId,
            request.ClientIdempotencyKey);

        IdempotencyRecord? ledger;
        try
        {
            ledger = await _idempotencyStore.GetAsync(identity.LedgerKey, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DependencyUnavailableResolution();
        }

        if (ledger is null || !string.Equals(
                ledger.OperationType,
                AllocationOperationType,
                StringComparison.Ordinal))
        {
            return NotFoundResolution();
        }

        IBlockchainOperation? operation = null;
        try
        {
            // The public-safe correlation can locate only the durable operation. The
            // raw ledger key remains inside this process for the idempotency read.
            var operationResult = await _operationStore.GetByIdempotencyKeyAsync(identity.Correlation, ct);
            if (operationResult.Exception is not null)
                return DependencyUnavailableResolution();
            if (operationResult.IsError && !IsAbsentOperation(operationResult))
            {
                return DependencyUnavailableResolution();
            }

            if (!operationResult.IsError && operationResult.Result is null)
                return DependencyUnavailableResolution();

            if (!operationResult.IsError && operationResult.Result is not null)
            {
                operation = operationResult.Result;
                if (!IsOwnedAllocation(operation, request, identity.Correlation))
                    return NotFoundResolution();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DependencyUnavailableResolution();
        }

        var result = ReadAllocationResult(ledger, operation);
        var state = ResolveState(ledger.State, operation?.Status);
        var updatedAt = operation?.CompletedDate is { } completedAt && completedAt > ledger.UpdatedAt
            ? completedAt
            : ledger.UpdatedAt;

        return AZOAResult<ReceiptResolution>.Success(new ReceiptResolution(
            operation,
            new AllocationReceiptResponse
            {
                ReceiptReference = identity.Correlation,
                OperationId = operation?.Id,
                State = state,
                IsTerminal = state != AllocationReceiptState.AwaitingReconciliation,
                RequiresReconciliation = state == AllocationReceiptState.AwaitingReconciliation,
                AvatarId = result?.AvatarId,
                WalletId = result?.WalletId,
                WalletAddress = result?.WalletAddress,
                WalletProvisioned = result?.WalletProvisioned,
                TransactionReference = ReadTransactionReference(operation),
                GrossAmount = result?.GrossAmount,
                NodeFeeAmount = result?.NodeFeeAmount,
                NetAmount = result?.NetAmount,
                NodeFeeScheduleVersion = result?.NodeFeeScheduleVersion,
                CreatedAt = ledger.CreatedAt,
                UpdatedAt = updatedAt,
                FailureCode = state == AllocationReceiptState.Failed ? AllocationFailedCode : null,
            }));
    }

    private static bool IsValid(AllocationReceiptRequest? request)
        => request is not null
           && request.ApiKeyId != Guid.Empty
           && request.CallerAvatarId != Guid.Empty
           && !string.IsNullOrWhiteSpace(request.ClientIdempotencyKey);

    private static bool IsOwnedAllocation(
        IBlockchainOperation operation,
        AllocationReceiptRequest request,
        string correlation)
        => string.Equals(operation.IdempotencyKey, correlation, StringComparison.Ordinal)
           && operation.InitiatorApiKeyId == request.ApiKeyId
           && operation.InitiatorAvatarId == request.CallerAvatarId;

    private static bool IsAbsentOperation(AZOAResult<IBlockchainOperation> result)
        => string.Equals(result.Code, AzoaErrorCodes.NotFound, StringComparison.Ordinal)
           || string.Equals(result.Message, "Operation not found.", StringComparison.Ordinal);

    private static AllocationReceiptState ResolveState(
        IdempotencyState ledgerState,
        string? operationStatus)
    {
        if (ledgerState == IdempotencyState.Failed ||
            string.Equals(operationStatus, OperationStatus.Failed, StringComparison.Ordinal))
        {
            return AllocationReceiptState.Failed;
        }

        return ledgerState == IdempotencyState.Completed && IsSuccessfulTerminal(operationStatus)
            ? AllocationReceiptState.Completed
            : AllocationReceiptState.AwaitingReconciliation;
    }

    private static bool IsSuccessfulTerminal(string? status)
        => status is OperationStatus.Completed
            or OperationStatus.Minted
            or OperationStatus.Burned
            or OperationStatus.Exchanged
            or OperationStatus.Swapped
            or OperationStatus.Transferred
            or OperationStatus.Deployed
            or OperationStatus.Called;

    private static AllocationResult? ReadAllocationResult(
        IdempotencyRecord ledger,
        IBlockchainOperation? operation)
    {
        var fromLedger = DeserializeAllocationResult(ledger.ResultPayload);
        if (fromLedger is not null)
            return fromLedger;

        if (operation is null)
            return null;

        operation.Parameters.TryGetValue(
            IdempotencyParameterNames.ResultPayload,
            out var operationPayload);
        return DeserializeAllocationResult(operationPayload);
    }

    private static AllocationResult? DeserializeAllocationResult(string? payload)
        => string.IsNullOrWhiteSpace(payload)
            ? null
            : IdempotencyReplay.DeserializeForReplay<AllocationResult>(payload);

    private static string? ReadTransactionReference(IBlockchainOperation? operation)
        => operation is not null && operation.Parameters.TryGetValue("TxHash", out var txHash) &&
           !string.IsNullOrWhiteSpace(txHash)
            ? txHash
            : null;

    private static AZOAResult<AllocationReceiptResponse> InvalidRequest()
        => AZOAResult<AllocationReceiptResponse>.FailureWithCode(
            "A caller API key, caller identity, and Idempotency-Key are required.",
            AzoaErrorCodes.InvalidRequest);

    private static AZOAResult<AllocationReceiptResponse> NotFound()
        => AZOAResult<AllocationReceiptResponse>.FailureWithCode(
            "Allocation receipt not found.",
            AzoaErrorCodes.NotFound);

    private static AZOAResult<AllocationReceiptResponse> DependencyUnavailable()
        => AZOAResult<AllocationReceiptResponse>.FailureWithCode(
            "Allocation receipt service is temporarily unavailable. Try again later.",
            AzoaErrorCodes.DependencyUnavailable);

    private static AZOAResult<ReceiptResolution> NotFoundResolution()
        => AZOAResult<ReceiptResolution>.FailureWithCode(
            "Allocation receipt not found.",
            AzoaErrorCodes.NotFound);

    private static AZOAResult<ReceiptResolution> DependencyUnavailableResolution()
        => AZOAResult<ReceiptResolution>.FailureWithCode(
            "Allocation receipt service is temporarily unavailable. Try again later.",
            AzoaErrorCodes.DependencyUnavailable);

    private static AZOAResult<AllocationReceiptResponse> ProjectResolution(
        AZOAResult<ReceiptResolution> resolution,
        string successMessage)
    {
        if (!resolution.IsError && resolution.Result is not null)
            return AZOAResult<AllocationReceiptResponse>.Success(resolution.Result.Response, successMessage);

        return string.Equals(resolution.Code, AzoaErrorCodes.NotFound, StringComparison.Ordinal)
            ? NotFound()
            : DependencyUnavailable();
    }

    private sealed record ReceiptResolution(
        IBlockchainOperation? Operation,
        AllocationReceiptResponse Response);
}
