using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Idempotency;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Managers;

public class BlockchainOperationManager : IBlockchainOperationManager
{
    private readonly IBlockchainOperationStore _blockchainOperationStore;
    private readonly IBlockchainProviderFactory _chainFactory;
    private readonly IIdempotencyStore _idempotencyStore;

    public BlockchainOperationManager(
        IBlockchainOperationStore blockchainOperationStore,
        IBlockchainProviderFactory chainFactory,
        IIdempotencyStore idempotencyStore)
    {
        _blockchainOperationStore = blockchainOperationStore;
        _chainFactory = chainFactory;
        _idempotencyStore = idempotencyStore;
    }

    public async Task<OASISResult<IBlockchainOperation>> ExecuteAsync(IBlockchainOperation operation, OASISRequest? request = null)
    {
        var chainType = operation.Parameters.GetValueOrDefault("ChainType", _chainFactory.GetDefaultProvider().ChainType);
        var networkStr = operation.Parameters.GetValueOrDefault("ChainNetwork", "Devnet");
        var network = Enum.TryParse<ChainNetwork>(networkStr, true, out var parsed) ? parsed : ChainNetwork.Devnet;

        // ── Idempotency gate (api-safety-hardening task 11) ──────────────────
        // The on-chain effect for this operation is irreversible. We must NOT
        // key the execution decision off the fresh-GUID operation.Id (a new
        // GUID every call ⇒ no dedupe). Instead derive a DETERMINISTIC,
        // content-addressed key from the stable logical inputs of the op so the
        // "same logical operation" (same chain/type/wallet/params) always maps
        // to the same key and executes its chain effect exactly once, even
        // under duplicate or concurrent requests.
        var idempotencyKey = DeriveIdempotencyKey(operation, chainType);

        var claim = await _idempotencyStore.TryClaimAsync(idempotencyKey, operation.OperationType, CancellationToken.None);
        if (!claim.Won)
        {
            // A duplicate / concurrent request already owns (or owned) this
            // logical operation. We MUST NOT re-execute the irreversible
            // effect — reconstruct and return the prior outcome.
            return ReplayFromRecord(operation, claim.Record);
        }

        // We won the claim: persist the operation row, then perform the effect
        // exactly once.
        var result = await _blockchainOperationStore.UpsertAsync(operation, default);
        if (result.IsError)
        {
            await _idempotencyStore.FailAsync(idempotencyKey, result.Message, CancellationToken.None);
            return result;
        }

        var chainProvider = _chainFactory.GetProvider(chainType, network);

        try
        {
            switch (operation.OperationType)
            {
                case "Mint":
                    await ExecuteMintAsync(operation, chainProvider);
                    break;
                case "Burn":
                    await ExecuteBurnAsync(operation, chainProvider);
                    break;
                case "Exchange":
                    await ExecuteExchangeAsync(operation, chainProvider);
                    break;
                case "Swap":
                    await ExecuteSwapAsync(operation, chainProvider);
                    break;
                case "Transfer":
                    await ExecuteTransferAsync(operation, chainProvider);
                    break;
                case "DeployContract":
                    await ExecuteDeployContractAsync(operation, chainProvider);
                    break;
                case "CallContract":
                    await ExecuteCallContractAsync(operation, chainProvider);
                    break;
                case "Composite":
                    break;
                default:
                    operation.Status = OperationStatus.Unknown;
                    break;
            }
        }
        catch (Exception ex)
        {
            operation.Status = OperationStatus.Failed;
            operation.Parameters["Error"] = ex.Message;
        }

        if (operation.Status == OperationStatus.Pending)
        {
            operation.Status = OperationStatus.Completed;
            operation.CompletedDate = DateTime.UtcNow;
        }

        var saved = await _blockchainOperationStore.UpsertAsync(operation, default);

        // Settle the idempotency record from the terminal operation state.
        // NOTE: an "AwaitingSignature" op has NOT been broadcast server-side
        // (it goes to client-side signing) — it is NOT yet irreversible, so we
        // must NOT mark the idempotency record Completed. Leave it InProgress so
        // a later server-side submit (if ever added) can still settle it; a
        // duplicate request meanwhile replays the same "awaiting signature"
        // instruction without re-running the (harmless, non-broadcasting) path.
        await SettleIdempotencyAsync(idempotencyKey, operation, saved);

        return saved;
    }

    /// <summary>
    /// Deterministic, content-addressed idempotency key for an operation. Built
    /// from the stable logical inputs (chain, type, wallet, and the op-type's
    /// value-bearing parameters) so identical logical requests collapse to one
    /// on-chain effect. Explicitly does NOT include <see cref="IBlockchainOperation.Id"/>
    /// (fresh GUID per call) or any timestamp.
    /// </summary>
    private static string DeriveIdempotencyKey(IBlockchainOperation operation, string chainType)
    {
        var p = operation.Parameters;
        var walletAddress = p.GetValueOrDefault("WalletAddress", string.Empty);

        // Per-op-type stable parameters that define "the same logical op".
        var opParams = operation.OperationType switch
        {
            "Mint" => new object[]
            {
                "mint",
                (operation as IMintOperation)?.TokenUri ?? p.GetValueOrDefault("TokenUri", string.Empty),
                (operation as IMintOperation)?.Amount.ToString() ?? p.GetValueOrDefault("Amount", string.Empty),
                (operation as IMintOperation)?.AssetType ?? p.GetValueOrDefault("AssetType", string.Empty),
            },
            "Burn" => new object[]
            {
                "burn",
                p.GetValueOrDefault("TokenId", string.Empty),
                p.GetValueOrDefault("Amount", string.Empty),
            },
            "Exchange" => new object[]
            {
                "exchange",
                p.GetValueOrDefault("SourceTokenId", (operation as IExchangeOperation)?.SourceHolonId?.ToString() ?? string.Empty),
                p.GetValueOrDefault("TargetTokenId", (operation as IExchangeOperation)?.TargetHolonId?.ToString() ?? string.Empty),
                (operation as IExchangeOperation)?.ExchangeRate ?? string.Empty,
            },
            "Swap" => new object[]
            {
                "swap",
                p.GetValueOrDefault("TokenIn", string.Empty),
                p.GetValueOrDefault("TokenOut", string.Empty),
                p.GetValueOrDefault("AmountIn", string.Empty),
                p.GetValueOrDefault("MinAmountOut", string.Empty),
            },
            "Transfer" => new object[]
            {
                "transfer",
                p.GetValueOrDefault("SourceTokenId", (operation as ITransferOperation)?.SourceHolonId?.ToString() ?? string.Empty),
                (operation as ITransferOperation)?.RecipientAddress ?? p.GetValueOrDefault("RecipientAddress", string.Empty),
            },
            "DeployContract" => new object[]
            {
                "deploy",
                // ContractCode is the full payload; hashing happens inside the
                // generator so length is bounded.
                p.GetValueOrDefault("ContractCode", string.Empty),
                p.GetValueOrDefault("Args", string.Empty),
            },
            "CallContract" => new object[]
            {
                "call",
                p.GetValueOrDefault("ContractAddress", string.Empty),
                p.GetValueOrDefault("Method", string.Empty),
                p.GetValueOrDefault("Args", string.Empty),
            },
            // Composite / Unknown: no value-bearing params; fall back to the
            // op type alone (still deterministic, still deduped).
            _ => new object[] { operation.OperationType.ToLowerInvariant() },
        };

        return OperationIdGenerator.Generate(chainType, operation.OperationType, walletAddress, opParams);
    }

    /// <summary>
    /// Reconstruct the prior outcome for a duplicate/concurrent request without
    /// re-performing the irreversible effect.
    /// </summary>
    private static OASISResult<IBlockchainOperation> ReplayFromRecord(
        IBlockchainOperation operation, IdempotencyRecord record)
    {
        switch (record.State)
        {
            case IdempotencyState.Completed:
                // Replay the cached terminal state (incl. TxHash).
                operation.Status = OperationStatus.Completed;
                operation.CompletedDate = record.UpdatedAt;
                if (!string.IsNullOrEmpty(record.ResultPayload))
                    operation.Parameters["TxHash"] = record.ResultPayload!;
                return new OASISResult<IBlockchainOperation>
                {
                    IsError = false,
                    Result = operation,
                    Message = "Duplicate request: returning the result of the original operation (not re-executed)."
                };

            case IdempotencyState.Failed:
                operation.Status = OperationStatus.Failed;
                if (!string.IsNullOrEmpty(record.Error))
                    operation.Parameters["Error"] = record.Error!;
                return new OASISResult<IBlockchainOperation>
                {
                    IsError = true,
                    Result = operation,
                    Message = record.Error ?? "Duplicate request: the original operation failed (not re-executed)."
                };

            case IdempotencyState.InProgress:
            default:
                // The original is still executing (or stopped mid-flight). Do
                // NOT re-broadcast. Surface a non-terminal "duplicate/pending"
                // status so the caller can poll/reconcile rather than re-send.
                operation.Status = OperationStatus.Pending;
                return new OASISResult<IBlockchainOperation>
                {
                    IsError = false,
                    Result = operation,
                    Message = "Duplicate request: the original operation is still in progress (not re-executed)."
                };
        }
    }

    /// <summary>
    /// Settle the idempotency record from the operation's terminal state. Only
    /// states whose on-chain effect has actually happened are made terminal:
    /// <list type="bullet">
    /// <item>Failed ⇒ <see cref="IIdempotencyStore.FailAsync"/>.</item>
    /// <item>A real success (TxHash present, not awaiting client signature) ⇒
    /// <see cref="IIdempotencyStore.CompleteAsync"/> with the TxHash.</item>
    /// <item>AwaitingSignature / no broadcast ⇒ left InProgress (not yet
    /// irreversible; nothing was submitted server-side).</item>
    /// </list>
    /// </summary>
    private async Task SettleIdempotencyAsync(
        string idempotencyKey, IBlockchainOperation operation, OASISResult<IBlockchainOperation> saved)
    {
        if (saved.IsError || operation.Status == OperationStatus.Failed)
        {
            var error = saved.IsError
                ? saved.Message
                : operation.Parameters.GetValueOrDefault("Error", "Operation failed");
            await _idempotencyStore.FailAsync(idempotencyKey, error, CancellationToken.None);
            return;
        }

        if (operation.Status == OperationStatus.AwaitingSignature)
        {
            // Not broadcast server-side — intentionally NOT completed. Leave
            // the claim InProgress; duplicates replay the awaiting-signature
            // state, and no double-broadcast is possible because no broadcast
            // occurred here at all.
            return;
        }

        if (operation.Status is OperationStatus.Unknown or OperationStatus.Pending)
        {
            // Nothing irreversible happened (unknown op type / no-op). Record
            // a benign failure so the key is terminal and a retry with the
            // same key does not silently hang as a perpetual duplicate.
            await _idempotencyStore.FailAsync(
                idempotencyKey, $"Operation not executed (status: {operation.Status}).", CancellationToken.None);
            return;
        }

        // Terminal success (Minted/Burned/Swapped/Transferred/...). Cache the
        // TxHash so duplicates replay the exact same on-chain result.
        var txHash = operation.Parameters.GetValueOrDefault("TxHash", string.Empty);
        await _idempotencyStore.CompleteAsync(idempotencyKey, txHash, CancellationToken.None);
    }

    public async Task<OASISResult<IBlockchainOperation>> BuildAndExecuteAsync(Func<BlockchainOperationBuilder, IBlockchainOperation> build, OASISRequest? request = null)
    {
        var builder = new BlockchainOperationBuilder();
        var operation = build(builder);
        return await ExecuteAsync(operation, request);
    }

    public async Task<OASISResult<IBlockchainOperation>> GetAsync(Guid id, Guid? avatarId = null, OASISRequest? request = null)
    {
        var result = await _blockchainOperationStore.GetByIdAsync(id, default);
        if (result.IsError || result.Result == null) return result;
        if (avatarId.HasValue && result.Result.AvatarId != avatarId.Value)
            return new OASISResult<IBlockchainOperation> { IsError = true, Message = "Operation is owned by a different avatar." };
        return result;
    }

    public async Task<OASISResult<IEnumerable<IBlockchainOperation>>> GetByAvatarAsync(Guid avatarId, OASISRequest? request = null)
    {
        return await _blockchainOperationStore.GetByAvatarAsync(avatarId, default);
    }

    private async Task ExecuteMintAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        if (operation is not IMintOperation mint) return;
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        var result = await chainProvider.MintAsync(
            mint.TokenUri ?? string.Empty, mint.Amount, mint.AssetType ?? string.Empty, walletAddress);
        ApplyChainResult(operation, result, OperationStatus.Minted);
    }

    private async Task ExecuteBurnAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        var tokenId = operation.Parameters.GetValueOrDefault("TokenId", string.Empty);
        var amount = int.Parse(operation.Parameters.GetValueOrDefault("Amount", "0"));
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        var result = await chainProvider.BurnAsync(tokenId, amount, walletAddress);
        ApplyChainResult(operation, result, OperationStatus.Burned);
    }

    private async Task ExecuteExchangeAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        if (operation is not IExchangeOperation exchange) return;
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        var sourceTokenId = operation.Parameters.GetValueOrDefault("SourceTokenId", exchange.SourceHolonId?.ToString() ?? string.Empty);
        var targetTokenId = operation.Parameters.GetValueOrDefault("TargetTokenId", exchange.TargetHolonId?.ToString() ?? string.Empty);
        var result = await chainProvider.ExchangeAsync(sourceTokenId, targetTokenId, exchange.ExchangeRate ?? string.Empty, walletAddress);
        ApplyChainResult(operation, result, OperationStatus.Exchanged);
    }

    private async Task ExecuteSwapAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        var tokenIn = operation.Parameters.GetValueOrDefault("TokenIn", string.Empty);
        var tokenOut = operation.Parameters.GetValueOrDefault("TokenOut", string.Empty);
        var amountIn = decimal.Parse(operation.Parameters.GetValueOrDefault("AmountIn", "0"));
        var minAmountOut = decimal.Parse(operation.Parameters.GetValueOrDefault("MinAmountOut", "0"));
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        var result = await chainProvider.SwapAsync(tokenIn, tokenOut, amountIn, minAmountOut, walletAddress);
        ApplyChainResult(operation, result, OperationStatus.Swapped);
    }

    private async Task ExecuteTransferAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        if (operation is not ITransferOperation transfer) return;
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        var sourceTokenId = operation.Parameters.GetValueOrDefault("SourceTokenId", transfer.SourceHolonId?.ToString() ?? string.Empty);
        var result = await chainProvider.TransferAsync(sourceTokenId, walletAddress, transfer.RecipientAddress ?? string.Empty, 1);
        ApplyChainResult(operation, result, OperationStatus.Transferred);
    }

    private async Task ExecuteDeployContractAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        var contractCode = Convert.FromBase64String(operation.Parameters.GetValueOrDefault("ContractCode", string.Empty));
        var args = operation.Parameters.GetValueOrDefault("Args") is string s && !string.IsNullOrEmpty(s)
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(s)
            : null;
        var result = await chainProvider.DeployContractAsync(contractCode, walletAddress, args);
        ApplyChainResult(operation, result, OperationStatus.Deployed);
    }

    private async Task ExecuteCallContractAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        var contractAddress = operation.Parameters.GetValueOrDefault("ContractAddress", string.Empty);
        var method = operation.Parameters.GetValueOrDefault("Method", string.Empty);
        var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
            operation.Parameters.GetValueOrDefault("Args", "{}")) ?? new();
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        var result = await chainProvider.CallContractAsync(contractAddress, method, args, walletAddress);
        ApplyChainResult(operation, result, OperationStatus.Called);
    }

    private static void ApplyChainResult<T>(IBlockchainOperation operation, OASISResult<T> chainResult, string successStatus)
    {
        if (chainResult.IsError)
        {
            operation.Status = OperationStatus.Failed;
            operation.Parameters["Error"] = chainResult.Message;
            return;
        }

        var message = chainResult.Message ?? "";
        var requiresSignature = message.Contains("Requires client-side signing") ||
                                message.Contains("Requires client-side") ||
                                message.Contains("Sign and submit");

        if (requiresSignature)
        {
            operation.Status = OperationStatus.AwaitingSignature;
            operation.Parameters["OperationId"] = chainResult.Result?.ToString() ?? string.Empty;
            operation.Parameters["Instruction"] = message;
        }
        else
        {
            operation.Status = successStatus;
            if (chainResult.Result != null)
                operation.Parameters["TxHash"] = chainResult.Result.ToString() ?? string.Empty;
        }
    }
}
