using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Services.Blockchain;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

public class BlockchainOperationManager : IBlockchainOperationManager
{
    private const string UnexpectedExecutionMessage =
        "Blockchain execution ended unexpectedly; automatic rebroadcast is disabled pending reconciliation.";

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

    public async Task<AZOAResult<IBlockchainOperation>> ExecuteAsync(IBlockchainOperation operation, AZOARequest? request = null)
    {
        string requestedChain;
        try
        {
            requestedChain = operation.Parameters.TryGetValue("ChainType", out var persistedChain)
                && !string.IsNullOrWhiteSpace(persistedChain)
                    ? persistedChain
                    : _chainFactory.GetDefaultProvider().ChainType;
        }
        catch (BlockchainProviderNotFoundException)
        {
            return AZOAResult<IBlockchainOperation>.Failure(
                "The configured default blockchain provider is unavailable.", operation);
        }

        var networkStr = operation.Parameters.GetValueOrDefault("ChainNetwork", "Devnet");
        if (!Enum.TryParse<ChainNetwork>(networkStr, true, out var network)
            || !Enum.IsDefined(network))
        {
            return new AZOAResult<IBlockchainOperation>
            {
                IsError = true,
                Result = operation,
                Message = $"Invalid ChainNetwork '{networkStr}'.",
            };
        }

        IBlockchainProvider chainProvider;
        try
        {
            chainProvider = _chainFactory.GetProvider(requestedChain, network);
        }
        catch (BlockchainProviderNotFoundException)
        {
            return AZOAResult<IBlockchainOperation>.Failure(
                $"No blockchain provider is available for {requestedChain}/{network}.", operation);
        }

        var chainType = chainProvider.ChainType;
        operation.Parameters["ChainType"] = chainType;
        operation.Parameters["ChainNetwork"] = network.ToString();
        operation.Parameters.Remove("TxHash");
        operation.Parameters.Remove("Error");

        // ── Idempotency gate (api-safety-hardening task 11) ──────────────────
        // The on-chain effect for this operation is irreversible. We must NOT
        // key the execution decision off the fresh-GUID operation.Id (a new
        // GUID every call ⇒ no dedupe). Instead derive a DETERMINISTIC,
        // content-addressed key from the stable logical inputs of the op so the
        // "same logical operation" (same chain/type/wallet/params) always maps
        // to the same key and executes its chain effect exactly once, even
        // under duplicate or concurrent requests.
        var idempotencyKey = DeriveIdempotencyKey(operation, chainType, network);

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
        try
        {
            var result = await _blockchainOperationStore.UpsertAsync(operation, default);
            if (result.IsError)
            {
                await _idempotencyStore.FailAsync(idempotencyKey, result.Message, CancellationToken.None);
                return result;
            }

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

            if (operation.Status == OperationStatus.Pending)
            {
                operation.Status = OperationStatus.Completed;
                operation.CompletedDate = DateTime.UtcNow;
            }

            var saved = await _blockchainOperationStore.UpsertAsync(operation, default);

            if (saved.IsError
                && !string.IsNullOrWhiteSpace(
                    operation.Parameters.GetValueOrDefault("TxHash", string.Empty)))
            {
                throw new InvalidOperationException(
                    "Post-broadcast operation persistence returned an error result.");
            }

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
        catch (Exception ex)
        {
            await RecoverClaimAfterUnexpectedFailureAsync(idempotencyKey, operation, ex);
            throw;
        }
    }

    /// <summary>
    /// Deterministic, content-addressed idempotency key for an operation. Built
    /// from the stable logical inputs (chain, type, wallet, and the op-type's
    /// value-bearing parameters) so identical logical requests collapse to one
    /// on-chain effect. Explicitly does NOT include <see cref="IBlockchainOperation.Id"/>
    /// (fresh GUID per call) or any timestamp.
    /// </summary>
    private static string DeriveIdempotencyKey(
        IBlockchainOperation operation,
        string chainType,
        ChainNetwork network)
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
                p.GetValueOrDefault("Amount", string.Empty),
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

        if (p.TryGetValue("IdempotencyKey", out var parentKey) && !string.IsNullOrWhiteSpace(parentKey))
            opParams = [.. opParams, "parent", parentKey];

        opParams = [.. opParams, "network", network.ToString()];

        return OperationIdGenerator.Generate(chainType, operation.OperationType, walletAddress, opParams);
    }

    /// <summary>
    /// Reconstruct the prior outcome for a duplicate/concurrent request without
    /// re-performing the irreversible effect.
    /// </summary>
    private static AZOAResult<IBlockchainOperation> ReplayFromRecord(
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
                return new AZOAResult<IBlockchainOperation>
                {
                    IsError = false,
                    Result = operation,
                    Message = "Duplicate request: returning the result of the original operation (not re-executed)."
                };

            case IdempotencyState.Failed:
                operation.Status = OperationStatus.Failed;
                if (!string.IsNullOrEmpty(record.Error))
                    operation.Parameters["Error"] = record.Error!;
                return new AZOAResult<IBlockchainOperation>
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
                return new AZOAResult<IBlockchainOperation>
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
        string idempotencyKey, IBlockchainOperation operation, AZOAResult<IBlockchainOperation> saved)
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

        if (operation.Status == OperationStatus.PendingConfirmation)
        {
            // value-path-wiring M1: the tx WAS broadcast but is not yet confirmed.
            // Leave the idempotency record InProgress so (a) a duplicate replays
            // "in progress" and NEVER re-broadcasts, and (b) reconciliation can
            // settle the claim Completed/Failed from chain truth once the recorded
            // TxHash resolves. Completing it now would lie; failing it would block a
            // legitimate slow-but-valid tx from settling.
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

    private async Task RecoverClaimAfterUnexpectedFailureAsync(
        string idempotencyKey,
        IBlockchainOperation operation,
        Exception originalException)
    {
        var txHash = operation.Parameters.GetValueOrDefault("TxHash", string.Empty);
        operation.Status = string.IsNullOrWhiteSpace(txHash)
            ? OperationStatus.Unknown
            : OperationStatus.PendingConfirmation;
        operation.Parameters["Error"] = UnexpectedExecutionMessage;

        var recoveryPersisted = false;
        try
        {
            var persisted = await _blockchainOperationStore.UpsertAsync(operation, CancellationToken.None);
            recoveryPersisted = !persisted.IsError;
            if (recoveryPersisted && operation.Status == OperationStatus.PendingConfirmation)
                return;

            if (persisted.IsError)
                originalException.Data["AZOA.OperationRecovery"] =
                    "Operation persistence returned an error result.";
        }
        catch (Exception recoveryException)
        {
            originalException.Data["AZOA.OperationRecoveryExceptionType"] =
                recoveryException.GetType().FullName ?? recoveryException.GetType().Name;
        }

        if (recoveryPersisted && operation.Status == OperationStatus.PendingConfirmation)
            return;

        await IdempotencyClaimRecovery.TryFailAsync(
            _idempotencyStore,
            idempotencyKey,
            UnexpectedExecutionMessage,
            originalException);
    }

    public async Task<AZOAResult<IBlockchainOperation>> BuildAndExecuteAsync(Func<BlockchainOperationBuilder, IBlockchainOperation> build, AZOARequest? request = null)
    {
        var builder = new BlockchainOperationBuilder();
        var operation = build(builder);
        return await ExecuteAsync(operation, request);
    }

    public async Task<AZOAResult<IBlockchainOperation>> GetAsync(Guid id, Guid? avatarId = null, AZOARequest? request = null)
    {
        var result = await _blockchainOperationStore.GetByIdAsync(id, default);
        if (result.IsError || result.Result == null) return result;
        if (avatarId.HasValue && result.Result.AvatarId != avatarId.Value)
            return new AZOAResult<IBlockchainOperation> { IsError = true, Message = "Operation is owned by a different avatar." };
        return result;
    }

    public async Task<AZOAResult<IEnumerable<IBlockchainOperation>>> GetByAvatarAsync(Guid avatarId, AZOARequest? request = null)
    {
        return await _blockchainOperationStore.GetByAvatarAsync(avatarId, default);
    }

    private async Task ExecuteMintAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        if (operation is not IMintOperation mint) return;
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        // D5: the typed ulong Amount field is the single source of truth for the
        // mint value — same field the idempotency key is derived from. No
        // Parameters["Amount"] side-channel, no int clamp.
        var amount = mint.Amount;
        // Mint = ASA-create — a platform/ASA-admin op (the platform is the asset
        // manager/reserve/freeze/clawback). tenant-consent-delegation AC4b: a Grant /
        // FungibleTokenCreate mint is platform-SIGNED yet tenant-DRIVEN — stamp the
        // acting tenant so the seam runs the live consent check before decrypt.
        var result = await chainProvider.MintAsync(
            mint.TokenUri ?? string.Empty, amount, mint.AssetType ?? string.Empty, walletAddress,
            BuildSigningContext(operation, platformOp: true));
        ApplyChainResult(operation, result, OperationStatus.Minted);
    }

    private async Task ExecuteBurnAsync(IBlockchainOperation operation, IBlockchainProvider chainProvider)
    {
        var tokenId = operation.Parameters.GetValueOrDefault("TokenId", string.Empty);
        var amount = ReadUlongAmount(operation, fallback: 0UL);
        var walletAddress = operation.Parameters.GetValueOrDefault("WalletAddress", string.Empty);
        // Burn = AssetDestroy — a platform/ASA-admin op signed by the asset manager.
        var result = await chainProvider.BurnAsync(tokenId, amount, walletAddress,
            BuildSigningContext(operation, platformOp: true));
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
        // value-path-wiring H4: amount defaults to 1 (single-asset move) when no
        // explicit Amount parameter is present, preserving the prior contract.
        var amount = ReadUlongAmount(operation, fallback: 1UL);
        // value-path-wiring C1: a transfer FROM a user's custodial wallet must sign
        // with the USER's key — build a per-user SigningContext from the op row so
        // the custody IDOR guard runs on the real value path. A platform-owned move
        // (no owning wallet/avatar on the row) signs with the platform key.
        var signingContext = BuildSigningContext(operation);
        var result = await chainProvider.TransferAsync(
            sourceTokenId, walletAddress, transfer.RecipientAddress ?? string.Empty, amount, signingContext);
        ApplyChainResult(operation, result, OperationStatus.Transferred);
    }

    /// <summary>
    /// value-path-wiring C1/D1: build the signer-resolution context from the op
    /// row. When the op carries an owning avatar + wallet, the move is a per-user
    /// custodial op and must sign with that user's key (custody IDOR-guarded);
    /// otherwise it is a platform-authority move signed by the platform key.
    /// <para>
    /// tenant-consent-delegation AC4/AC4b: when the op carries an
    /// <see cref="IBlockchainOperation.ActingTenantId"/> (a tenant drove this value
    /// op via a child credential), stamp the acting tenant + signing scope + grantor
    /// onto the context so the custody seam runs the LIVE consent check before any
    /// decrypt — on BOTH the platform and the user signing path. The grantor is the
    /// op's owning avatar (the on-whose-behalf user); for a platform op with no
    /// owning avatar a tenant-driven sign with no resolvable grantor is denied at the
    /// gate (fail-closed).
    /// </para>
    /// </summary>
    /// <param name="platformOp">When true, the base context is the platform key
    /// (Mint/Burn = ASA-admin) rather than a per-user resolve.</param>
    private static SigningContext BuildSigningContext(IBlockchainOperation operation, bool platformOp = false)
    {
        SigningContext baseCtx;
        if (!platformOp
            && operation.AvatarId is { } avatarId && avatarId != Guid.Empty
            && operation.WalletId is { } walletId && walletId != Guid.Empty)
        {
            baseCtx = SigningContext.ForUser(avatarId, walletId);
        }
        else
        {
            baseCtx = SigningContext.Platform;
        }

        // No acting tenant ⇒ user-driven / platform-internal; no grant required.
        if (operation.ActingTenantId is not { } tenantId || tenantId == Guid.Empty)
            return baseCtx;

        // S6 guardrail: the declared signing scope MUST be a legitimate scope for this
        // operation type. A mislabelled op (e.g. a Transfer stamped nft:mint) would let
        // the gate check the wrong capability — deny it before it can reach a decrypt.
        // Throwing here is caught by ExecuteAsync and marks the op Failed (fail-closed).
        if (!AzoaScopes.IsScopeValidForOperation(operation.OperationType, operation.SigningScope))
            throw new InvalidOperationException(
                $"Signing scope '{operation.SigningScope}' is not valid for operation type " +
                $"'{operation.OperationType}'; refusing to build a tenant-driven signing context.");

        // Tenant-driven: thread the acting tenant + scope + grantor to the seam.
        // The grantor is the op's owning avatar (the user on whose behalf the tenant
        // acts) — present on both user-key and platform-signed-on-behalf ops.
        var grantor = operation.AvatarId is { } a && a != Guid.Empty ? a : Guid.Empty;
        return baseCtx.ActingAs(tenantId, operation.SigningScope ?? string.Empty, grantor);
    }

    /// <summary>
    /// value-path-wiring H4: read the value amount as <see cref="ulong"/> from the
    /// op's <c>Amount</c> parameter (arbitrary-precision string), falling back to
    /// the supplied value when the parameter is absent or unparseable.
    /// </summary>
    private static ulong ReadUlongAmount(IBlockchainOperation operation, ulong fallback)
    {
        var raw = operation.Parameters.GetValueOrDefault("Amount", string.Empty);
        return ulong.TryParse(raw, out var parsed) ? parsed : fallback;
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

    private static void ApplyChainResult<T>(IBlockchainOperation operation, AZOAResult<T> chainResult, string successStatus)
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

        // value-path-wiring M1: a "submitted, pending confirmation" success means the
        // tx WAS broadcast (it carries a real tx id in Result) but did not confirm in
        // the poll window. Record the op PendingConfirmation + Parameters["TxHash"]
        // (NOT Failed, NOT a terminal success) so reconciliation settles it from
        // chain truth and a duplicate replays "in progress" rather than re-broadcasts.
        var pendingConfirmation = message.StartsWith(
            OperationStatus.PendingConfirmationMarker, StringComparison.Ordinal);

        if (requiresSignature)
        {
            operation.Status = OperationStatus.AwaitingSignature;
            operation.Parameters["OperationId"] = chainResult.Result?.ToString() ?? string.Empty;
            operation.Parameters["Instruction"] = message;
        }
        else if (pendingConfirmation)
        {
            operation.Status = OperationStatus.PendingConfirmation;
            if (chainResult.Result != null)
                operation.Parameters["TxHash"] = chainResult.Result.ToString() ?? string.Empty;
        }
        else
        {
            operation.Status = successStatus;
            if (chainResult.Result != null)
                operation.Parameters["TxHash"] = chainResult.Result.ToString() ?? string.Empty;
        }
    }
}
