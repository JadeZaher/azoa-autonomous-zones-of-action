// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Composes the existing KYC gate, wallet provisioning, and mint/transfer
/// primitives into one idempotent, KYC-gated, tenant-callable allocation seam
/// (see <see cref="IAllocationManager"/>). Holds no payment-provider secret and
/// applies the node's configured fee schedule to the tenant-decided gross amount,
/// then materialises the wallet and moves the net asset amount exactly once.
/// </summary>
public sealed class AllocationManager : IAllocationManager
{
    private const string OperationType = "fiat_allocation";
    private const string UnexpectedFailureMessage = "Allocation failed unexpectedly.";

    private readonly IKycGateService _kycGate;
    private readonly IWalletManager _walletManager;
    private readonly IWalletStore _walletStore;
    private readonly IHolonStore _holonStore;
    private readonly INftManager _nftManager;
    private readonly IBlockchainOperationManager _blockchainOps;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly INodeGovernanceGuard _nodeGovernance;
    private readonly INodeFeeScheduleManager _nodeFees;

    public AllocationManager(
        IKycGateService kycGate,
        IWalletManager walletManager,
        IWalletStore walletStore,
        IHolonStore holonStore,
        INftManager nftManager,
        IBlockchainOperationManager blockchainOps,
        IIdempotencyStore idempotencyStore,
        INodeFeeScheduleManager nodeFees,
        INodeGovernanceGuard? nodeGovernance = null)
    {
        _kycGate = kycGate ?? throw new ArgumentNullException(nameof(kycGate));
        _walletManager = walletManager ?? throw new ArgumentNullException(nameof(walletManager));
        _walletStore = walletStore ?? throw new ArgumentNullException(nameof(walletStore));
        _holonStore = holonStore ?? throw new ArgumentNullException(nameof(holonStore));
        _nftManager = nftManager ?? throw new ArgumentNullException(nameof(nftManager));
        _blockchainOps = blockchainOps ?? throw new ArgumentNullException(nameof(blockchainOps));
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _nodeGovernance = nodeGovernance ?? NodeGovernanceGuard.Unrestricted;
        _nodeFees = nodeFees ?? throw new ArgumentNullException(nameof(nodeFees));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<AllocationResult>> AllocateAsync(
        Guid avatarId,
        AllocationRequest request,
        Guid callerAvatarId,
        string? clientIdempotencyKey,
        string apiKeyId,
        Guid? actingTenantId = null)
    {
        if (request is null)
            return AZOAResult<AllocationResult>.Failure("Allocation request is required.");
        if (string.IsNullOrWhiteSpace(request.ChainType))
            return AZOAResult<AllocationResult>.Failure("ChainType is required.");
        if (string.IsNullOrWhiteSpace(apiKeyId))
            return AZOAResult<AllocationResult>.Failure("Caller API key context is required.");

        var governance = await _nodeGovernance.EnsureAllowedAsync(
            request.ChainType,
            ResolveGovernedAssetType(request),
            $"allocation:{request.Kind}");
        if (governance.IsError)
            return AZOAResult<AllocationResult>.Failure(governance.Message);

        // ── Step 1: idempotency key ───────────────────────────────────────────
        // Client key wins; absent ⇒ deterministic content key over
        // (avatarId, asset descriptor, amount). NEVER a random per-request key.
        // The whole key is partitioned by apiKeyId so two tenants reusing the
        // same human-friendly key (e.g. "1") cannot collide.
        var idempotencyKey = BuildIdempotencyKey(apiKeyId, avatarId, request, clientIdempotencyKey);

        // ── Step 2: idempotency claim ─────────────────────────────────────────
        // TryClaim BEFORE any irreversible effect. On a lost claim we replay the
        // stored original result and perform NO second mint/transfer.
        var claim = await _idempotencyStore.TryClaimAsync(idempotencyKey, OperationType, CancellationToken.None);
        if (!claim.Won)
            return ReplayFromRecord(claim.Record, idempotencyKey);

        var effectMayHaveStarted = false;
        try
        {
            // ── Step 3: KYC gate (fail-closed) ────────────────────────────────
            // Per D3, the value-bearing allocation is gated. Provisioning may
            // precede approval, but we gate before generating the wallet too so
            // a rejected avatar produces NO side effect at all under a won claim.
            var gate = await _kycGate.RequireVerifiedAsync(avatarId);
            if (gate.IsError)
            {
                await _idempotencyStore.FailAsync(idempotencyKey, gate.Message, CancellationToken.None);
                return AZOAResult<AllocationResult>.Failure(gate.Message);
            }

            // ── Step 4: parse the amount (H4) — reject BEFORE any broadcast ───
            // AllocationRequest.Amount is an arbitrary-precision string; the
            // provider value surface is ulong. A non-numeric / negative /
            // overflowing amount fails the idempotency key here so the claim is
            // terminal (Failed), never leaked InProgress, and nothing is broadcast.
            if (!TryParseAmount(request.Amount, out var amount, out var amountError))
            {
                await _idempotencyStore.FailAsync(idempotencyKey, amountError, CancellationToken.None);
                return AZOAResult<AllocationResult>.Failure(amountError);
            }

            var feeQuoteResult = await QuoteAllocationFeeAsync(request.Kind, amount);
            if (feeQuoteResult.IsError || feeQuoteResult.Result is null)
            {
                var msg = feeQuoteResult.IsError ? feeQuoteResult.Message : "Node fee quote produced no result.";
                await _idempotencyStore.FailAsync(idempotencyKey, msg, CancellationToken.None);
                return AZOAResult<AllocationResult>.Failure(msg);
            }
            var feeQuote = feeQuoteResult.Result;

            // ── Step 5: provision-if-absent ───────────────────────────────────
            var (wallet, provisioned, walletError) = await EnsureWalletAsync(avatarId, request.ChainType);
            if (wallet is null)
            {
                await _idempotencyStore.FailAsync(idempotencyKey, walletError, CancellationToken.None);
                return AZOAResult<AllocationResult>.Failure(walletError);
            }

            // ── Step 6: execute allocation (D4 discriminator) ─────────────────
            // The broadcast goes through IBlockchainOperationManager.ExecuteAsync so
            // the provider is actually called and a TxHash is recorded (D2). The
            // alloc idempotency key is persisted on the op row (H1) so a crashed
            // claim is recoverable by reconciliation.
            effectMayHaveStarted = true;
            var opResult = request.Kind switch
            {
                AllocationKind.Mint => await MintAsync(
                    avatarId, wallet, provisioned, request, feeQuote, idempotencyKey, actingTenantId),
                AllocationKind.Transfer => await TransferAsync(
                    avatarId, callerAvatarId, wallet, provisioned, request, feeQuote,
                    idempotencyKey, actingTenantId),
                _ => Operation.Invalid($"Unsupported allocation kind: {request.Kind}.")
            };

            if (opResult.IsError || opResult.Result is null)
            {
                var msg = opResult.IsError ? opResult.Message : "Allocation produced no operation.";
                await _idempotencyStore.FailAsync(idempotencyKey, msg, CancellationToken.None);
                return AZOAResult<AllocationResult>.Failure(msg);
            }

            var op = opResult.Result;

            // ── Step 7: settle the alloc idempotency key from the op outcome ──
            // C2: complete the allocation idempotency key ONLY when the op carries a
            // real TxHash (a confirmed broadcast, or an M1 pending-with-hash). If the
            // op did not record a TxHash, leave the allocation record InProgress so a
            // redelivered request replays "in progress" — NOT a false success.
            var result = BuildAllocationResult(
                avatarId, wallet, provisioned, op.Id, idempotencyKey, feeQuote);

            var txHash = op.Parameters.GetValueOrDefault("TxHash", string.Empty);
            if (string.IsNullOrWhiteSpace(txHash))
            {
                // Broadcast did not (yet) record a TxHash. Do NOT Complete the key —
                // leave it InProgress so reconciliation/replay can settle it from
                // chain truth. Surface a retryable in-progress state to the caller.
                return AZOAResult<AllocationResult>.Failure(
                    "Allocation broadcast is in progress; the on-chain effect has not yet " +
                    "recorded a transaction hash. Retry once it settles.");
            }

            await _idempotencyStore.CompleteAsync(
                idempotencyKey, SerializeForReplay(result), CancellationToken.None);

            return new AZOAResult<AllocationResult> { Result = result, Message = "Allocation completed." };
        }
        catch (Exception ex)
        {
            if (!effectMayHaveStarted)
                await IdempotencyClaimRecovery.TryFailAsync(
                    _idempotencyStore, idempotencyKey, UnexpectedFailureMessage, ex);
            throw;
        }
    }

    // ── Provision-if-absent ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the avatar's existing wallet for the chain, or generates one.
    /// Never duplicates (uniqueness is also guarded inside GenerateWalletAsync).
    /// </summary>
    private async Task<(IWallet? Wallet, bool Provisioned, string Error)> EnsureWalletAsync(
        Guid avatarId, string chainType)
    {
        var existing = await _walletStore.GetByAvatarAsync(avatarId);
        if (existing.IsError)
            return (null, false, existing.Message);

        if (!existing.IsError && existing.Result is not null)
        {
            var match = existing.Result.FirstOrDefault(w =>
                string.Equals(w.ChainType, chainType, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return (match, false, string.Empty);
        }

        var gen = await _walletManager.GenerateWalletAsync(
            new WalletGenerateRequest { ChainType = chainType }, avatarId);
        if (gen.IsError || gen.Result is null)
            return (null, false, gen.IsError ? gen.Message : "Wallet provisioning failed.");

        return (gen.Result, true, string.Empty);
    }

    // ── Allocation execution (consumes existing surface verbatim) ──────────────

    private async Task<AZOAResult<IBlockchainOperation>> MintAsync(
        Guid avatarId, IWallet wallet, bool walletProvisioned, AllocationRequest request,
        AllocationFeeQuote feeQuote,
        string idempotencyKey,
        Guid? actingTenantId = null)
    {
        // Step A: record allocation metadata within the already-owned claim.
        var mint = new NftMintRequest
        {
            WalletId = wallet.Id,
            Name = request.Name,
            Description = request.Description ?? string.Empty,
            ChainId = request.ChainType,
            TokenId = request.AssetId,
            Metadata = MergeAmount(request, feeQuote)
        };
        var holonResult = await _holonStore.UpsertAsync(NftHolonFactory.Create(mint, avatarId));
        if (holonResult.IsError || holonResult.Result is null)
            return Operation.Invalid(holonResult.Message);

        // Step B: build a typed mint op and drive it through the REAL broadcast
        // path (C2) so the provider is called and a TxHash is recorded.
        var op = new BlockchainOperation
        {
            AvatarId = avatarId,
            WalletId = wallet.Id,
            OperationType = "Mint",
            Status = OperationStatus.Pending,
            // IMintOperation typed fields drive both the idempotency-key derivation
            // and the ExecuteMintAsync provider call.
            TokenUri = request.AssetId ?? request.Name,
            AssetType = request.Name,
            // D5: the typed ulong field is the SINGLE source of truth for the mint
            // value — it drives both the idempotency-key derivation and the provider
            // call. No int clamp, no Parameters["Amount"] side-channel for mint.
            Amount = feeQuote.NetAmount,
            // AC4: stamp the acting tenant + signing scope when tenant-driven so the
            // custody seam runs the live consent check before key decrypt.
            ActingTenantId = actingTenantId,
            SigningScope = actingTenantId.HasValue ? AzoaScopes.NftMint : null,
            Parameters = BuildOpParameters(wallet, request, feeQuote, idempotencyKey, holonResult.Result.Id.ToString())
        };
        op.Parameters[IdempotencyParameterNames.ResultPayload] = SerializeForReplay(BuildAllocationResult(
            avatarId, wallet, walletProvisioned, op.Id, idempotencyKey, feeQuote));

        return await _blockchainOps.ExecuteAsync(op);
    }

    private async Task<AZOAResult<IBlockchainOperation>> TransferAsync(
        Guid targetAvatarId,
        Guid sourceAvatarId,
        IWallet targetWallet,
        bool walletProvisioned,
        AllocationRequest request,
        AllocationFeeQuote feeQuote,
        string idempotencyKey,
        Guid? actingTenantId = null)
    {
        if (request.AssetRecordId is null || request.AssetRecordId == Guid.Empty)
            return Operation.Invalid("Transfer allocation requires AssetRecordId.");
        if (sourceAvatarId == targetAvatarId)
            return Operation.Invalid("Transfer allocation source and target avatars must differ.");

        var (sourceWallet, sourceWalletError) = await FindWalletAsync(sourceAvatarId, request.ChainType);
        if (sourceWallet is null)
            return Operation.Invalid(sourceWalletError);

        // Step A: record the ownership move via NftManager (Holon upsert + IDOR
        // guard). The transfer target is the authorised avatar, never a body id.
        var transfer = new NftTransferRequest
        {
            TargetAvatarId = targetAvatarId,
            WalletId = targetWallet.Id,
            Memo = request.Memo
        };
        var holonResult = await _nftManager.TransferAsync(
            request.AssetRecordId.Value, transfer, sourceAvatarId);
        if (holonResult.IsError || holonResult.Result is null)
            return Operation.Invalid(holonResult.Message);
        var holonId = holonResult.Result.Parameters?.GetValueOrDefault("holonId");

        // Step B: build a typed transfer op and broadcast it (C2). The recipient is
        // the target avatar's custodial wallet address; the asset is request.AssetId.
        var op = new BlockchainOperation
        {
            AvatarId = sourceAvatarId,
            WalletId = sourceWallet.Id,
            OperationType = "Transfer",
            Status = OperationStatus.Pending,
            SourceHolonId = request.AssetRecordId,
            RecipientAddress = targetWallet.Address,
            // AC4: tenant-driven transfer signs with the USER's key — gate it.
            ActingTenantId = actingTenantId,
            SigningScope = actingTenantId.HasValue ? AzoaScopes.TransferSign : null,
            Parameters = BuildOpParameters(
                sourceWallet, request, feeQuote, idempotencyKey,
                holonId)
        };
        if (!string.IsNullOrWhiteSpace(request.AssetId))
            op.Parameters["SourceTokenId"] = request.AssetId!;
        op.Parameters[IdempotencyParameterNames.ResultPayload] = SerializeForReplay(BuildAllocationResult(
            targetAvatarId, targetWallet, walletProvisioned, op.Id, idempotencyKey, feeQuote));

        return await _blockchainOps.ExecuteAsync(op);
    }

    private async Task<(IWallet? Wallet, string Error)> FindWalletAsync(Guid avatarId, string chainType)
    {
        var wallets = await _walletStore.GetByAvatarAsync(avatarId);
        if (wallets.IsError)
            return (null, wallets.Message);

        var wallet = wallets.Result?.FirstOrDefault(w =>
            string.Equals(w.ChainType, chainType, StringComparison.OrdinalIgnoreCase));
        return wallet is null
            ? (null, $"Source avatar has no {chainType} wallet for this transfer.")
            : (wallet, string.Empty);
    }

    /// <summary>
    /// Build the op Parameters carrying everything the broadcast + reconciliation
    /// need: the wallet address the provider signs/moves from, the asset
    /// descriptor, the arbitrary-precision <c>Amount</c> string (the value channel
    /// for op types with NO typed amount field — Transfer/Burn), the chain, and
    /// (H1) the alloc idempotency key so an orphaned claim can be released by
    /// <c>ReconciliationService</c>. For Mint the typed <see cref="IMintOperation.Amount"/>
    /// ulong is authoritative; this string entry is the fallback for the typeless
    /// op types and storage rehydration.
    /// </summary>
    private static Dictionary<string, string> BuildOpParameters(
        IWallet wallet,
        AllocationRequest request,
        AllocationFeeQuote feeQuote,
        string idempotencyKey,
        string? holonId)
    {
        var p = new Dictionary<string, string>
        {
            ["WalletAddress"] = wallet.Address,
            ["Amount"] = FormatAmount(feeQuote.NetAmount),
            ["GrossAmount"] = FormatAmount(feeQuote.GrossAmount),
            ["NodeFeeAmount"] = FormatAmount(feeQuote.FeeAmount),
            ["NetAmount"] = FormatAmount(feeQuote.NetAmount),
            ["NodeFeeScheduleVersion"] = feeQuote.ScheduleVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["AssetType"] = request.Name,
            ["ChainType"] = request.ChainType,
            // H1: persist the alloc:{apiKeyId}:… key so reconciliation can settle an
            // orphaned InProgress claim from chain truth (bridge precedent).
            ["IdempotencyKey"] = idempotencyKey,
        };
        if (!string.IsNullOrWhiteSpace(request.AssetId))
            p["TokenUri"] = request.AssetId!;
        if (!string.IsNullOrWhiteSpace(holonId))
            p["holonId"] = holonId;
        return p;
    }

    /// <summary>
    /// Folds the already-decided amount into the asset metadata so it is recorded
    /// against the operation. AZOA treats the amount as opaque/authoritative.
    /// </summary>
    private static Dictionary<string, string> MergeAmount(
        AllocationRequest request,
        AllocationFeeQuote feeQuote)
    {
        var metadata = new Dictionary<string, string>(request.Metadata);
        metadata["amount"] = FormatAmount(feeQuote.NetAmount);
        metadata["GrossAmount"] = FormatAmount(feeQuote.GrossAmount);
        metadata["NodeFeeAmount"] = FormatAmount(feeQuote.FeeAmount);
        metadata["NetAmount"] = FormatAmount(feeQuote.NetAmount);
        metadata["NodeFeeScheduleVersion"] = feeQuote.ScheduleVersion.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        return metadata;
    }

    private async Task<AZOAResult<AllocationFeeQuote>> QuoteAllocationFeeAsync(
        AllocationKind kind,
        ulong grossAmount)
    {
        if (!TryResolveFeeOperation(kind, out var operation))
            return AZOAResult<AllocationFeeQuote>.Failure($"Unsupported allocation kind: {kind}.");

        var quote = await _nodeFees.QuoteAsync(operation, grossAmount);
        if (quote.IsError || quote.Result is null)
            return AZOAResult<AllocationFeeQuote>.Failure(
                quote.IsError ? quote.Message : "Node fee quote produced no result.");

        var converted = ToAllocationFeeQuote(quote.Result, grossAmount);
        if (converted.IsError || converted.Result is null)
            return converted;

        if (operation == NodeFeeOperation.Transfer && converted.Result.FeeAmount > 0)
        {
            return AZOAResult<AllocationFeeQuote>.Failure(
                "Transfer node fees require on-chain treasury settlement, which is not configured; allocation denied.");
        }

        return converted;
    }

    private static AZOAResult<AllocationFeeQuote> ToAllocationFeeQuote(
        NodeFeeQuoteResponse quote,
        ulong expectedGrossAmount)
    {
        if (!TryParseQuoteAmount(quote.GrossAmount, "gross", out var gross, out var error) ||
            !TryParseQuoteAmount(quote.FeeAmount, "fee", out var fee, out error) ||
            !TryParseQuoteAmount(quote.NetAmount, "net", out var net, out error))
            return AZOAResult<AllocationFeeQuote>.Failure(error);

        if (gross != expectedGrossAmount)
            return AZOAResult<AllocationFeeQuote>.Failure(
                "Node fee quote gross amount does not match the requested allocation amount.");
        if (fee >= gross || net == 0 || net != gross - fee)
            return AZOAResult<AllocationFeeQuote>.Failure(
                "Node fee quote is internally inconsistent or leaves no allocatable amount.");
        if (quote.ScheduleVersion < 0)
            return AZOAResult<AllocationFeeQuote>.Failure(
                "Node fee quote schedule version cannot be negative.");

        return new AZOAResult<AllocationFeeQuote>
        {
            Result = new AllocationFeeQuote(gross, fee, net, quote.ScheduleVersion),
            Message = "Success",
        };
    }

    private static bool TryResolveFeeOperation(AllocationKind kind, out NodeFeeOperation operation)
    {
        switch (kind)
        {
            case AllocationKind.Mint:
                operation = NodeFeeOperation.Mint;
                return true;
            case AllocationKind.Transfer:
                operation = NodeFeeOperation.Transfer;
                return true;
            default:
                operation = default;
                return false;
        }
    }

    private static bool TryParseQuoteAmount(
        string raw,
        string fieldName,
        out ulong amount,
        out string error)
    {
        if (ulong.TryParse(
                raw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out amount))
        {
            error = string.Empty;
            return true;
        }

        error = $"Node fee quote {fieldName} amount is not a valid unsigned 64-bit base-unit value.";
        return false;
    }

    private static string FormatAmount(ulong amount)
        => amount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static AllocationResult BuildAllocationResult(
        Guid avatarId,
        IWallet wallet,
        bool walletProvisioned,
        Guid operationId,
        string idempotencyKey,
        AllocationFeeQuote feeQuote)
        => new()
        {
            AvatarId = avatarId,
            WalletId = wallet.Id,
            WalletAddress = wallet.Address,
            WalletProvisioned = walletProvisioned,
            OperationId = operationId,
            Replayed = false,
            IdempotencyKey = idempotencyKey,
            GrossAmount = FormatAmount(feeQuote.GrossAmount),
            NodeFeeAmount = FormatAmount(feeQuote.FeeAmount),
            NetAmount = FormatAmount(feeQuote.NetAmount),
            NodeFeeScheduleVersion = feeQuote.ScheduleVersion,
        };

    private static string? ResolveGovernedAssetType(AllocationRequest request)
    {
        if (request.Metadata.TryGetValue("assetType", out var assetType) &&
            !string.IsNullOrWhiteSpace(assetType))
            return assetType;

        return string.IsNullOrWhiteSpace(request.Name) ? null : request.Name;
    }

    /// <summary>
    /// Parse the arbitrary-precision <see cref="AllocationRequest.Amount"/> string
    /// into the provider's <see cref="ulong"/> surface (H4 / D5). Rejects
    /// non-numeric, negative, and overflowing values with a clear error so the
    /// idempotency key is failed (no leak) before any broadcast.
    /// </summary>
    private static bool TryParseAmount(string raw, out ulong amount, out string error)
    {
        amount = 0;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Allocation amount is required.";
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('-'))
        {
            error = $"Allocation amount '{raw}' is negative; a non-negative integer base-unit amount is required.";
            return false;
        }

        if (!ulong.TryParse(trimmed, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out amount))
        {
            error =
                $"Allocation amount '{raw}' is not a valid non-negative integer in base units " +
                $"(must fit in an unsigned 64-bit range, max {ulong.MaxValue}).";
            return false;
        }

        if (amount == 0)
        {
            error = "Allocation amount must be a positive integer base-unit amount.";
            return false;
        }

        return true;
    }

    // ── Idempotency helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Builds the partitioned idempotency key. Always prefixed with the API-key
    /// id so the dedup namespace is per-tenant. The tail is the client key when
    /// present, else a deterministic SHA-256 over the allocation content.
    /// </summary>
    private static string BuildIdempotencyKey(
        string apiKeyId, Guid avatarId, AllocationRequest request, string? clientIdempotencyKey)
    {
        var tail = !string.IsNullOrWhiteSpace(clientIdempotencyKey)
            ? clientIdempotencyKey.Trim()
            : DeterministicContentKey(avatarId, request);
        return $"alloc:{apiKeyId}:{tail}";
    }

    private static string DeterministicContentKey(Guid avatarId, AllocationRequest request)
    {
        var canonical = string.Join('|',
            avatarId.ToString("N"),
            request.Kind.ToString(),
            request.ChainType.ToLowerInvariant(),
            request.Amount,
            request.AssetId ?? string.Empty,
            request.AssetRecordId?.ToString("N") ?? string.Empty);
        return IdempotencyReplay.ContentHash(canonical);
    }

    private static AZOAResult<AllocationResult> ReplayFromRecord(
        IdempotencyRecord record, string idempotencyKey)
        => IdempotencyReplay.ReplayFromRecord<AllocationResult>(
            record,
            DeserializeForReplay,
            r => r.Replayed = true,
            "Duplicate request: returning the result of the original allocation (not re-executed).",
            "Duplicate request: original allocation result could not be replayed.",
            "Original allocation failed.",
            $"Allocation for key '{idempotencyKey}' is already in progress; " +
            "retry once the original request settles.");

    private static string SerializeForReplay(AllocationResult result)
        => IdempotencyReplay.SerializeForReplay(result);

    private static AllocationResult? DeserializeForReplay(string payload)
        => IdempotencyReplay.DeserializeForReplay<AllocationResult>(payload);

    private sealed record AllocationFeeQuote(
        ulong GrossAmount,
        ulong FeeAmount,
        ulong NetAmount,
        long ScheduleVersion);

    /// <summary>Local helper for synthesising an error operation result.</summary>
    private static class Operation
    {
        public static AZOAResult<IBlockchainOperation> Invalid(string message)
            => new() { IsError = true, Message = message };
    }
}
