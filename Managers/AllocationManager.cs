// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
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
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Hold;
// ReserveDrawAuthority / ReservePartitionScope: the one who-may-draw-this-partition
// predicate, enforced inside PlatformReserveService.DrawAsync so every draw seam asks it.
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Composes value readiness, wallet provisioning, recipient preparation and the
/// reserve's issue/transfer primitives into one idempotent, readiness-gated,
/// tenant-callable allocation seam (see <see cref="IAllocationManager"/>). Holds no
/// payment-provider secret and applies the node's configured fee schedule to the
/// tenant-decided gross amount, then materialises the wallet and moves the net amount
/// exactly once.
/// </summary>
public sealed class AllocationManager : IAllocationManager
{
    private const string OperationType = "fiat_allocation";
    private const string UnexpectedFailureMessage = "Allocation failed unexpectedly.";
    private const int MaxPurposeLength = 256;
    private const int MaxPartitionLength = 64;

    private readonly IValueAccessService _valueAccess;
    private readonly IWalletManager _walletManager;
    private readonly IWalletStore _walletStore;
    private readonly IHolonStore _holonStore;
    private readonly INftManager _nftManager;
    private readonly IBlockchainOperationManager _blockchainOps;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly INodeGovernanceGuard _nodeGovernance;
    private readonly INodeFeeScheduleManager _nodeFees;
    private readonly IUserStore? _users;
    private readonly IPlatformReserve? _reserve;
    private readonly ICustodialRecipientPreparer? _recipientPreparer;
    private readonly INodeFeeSettlementManager? _feeSettlement;
    private readonly INodeTreasuryManager? _treasury;
    private readonly IBlockchainProviderFactory? _providers;
    private readonly ReserveOptions _reserveOptions;
    private readonly ILogger<AllocationManager>? _logger;

    /// <param name="reserve">
    /// The platform reserve every issued unit comes from (W3.1). Optional so the many
    /// hand-constructed instances in tests stay short; <see cref="AllocationKind.IssueUnits"/>
    /// REFUSES when it is absent rather than falling back to creating supply.
    /// </param>
    /// <param name="recipientPreparer">
    /// Makes a never-used custodial account able to hold the asset (W3.3). Same
    /// optional-but-fail-closed contract as <paramref name="reserve"/>.
    /// </param>
    /// <param name="feeSettlement">Durable obligation a non-zero transfer fee is admitted into.</param>
    /// <param name="treasury">Resolves where a non-zero transfer fee settles.</param>
    /// <param name="providers">Read for the configured network only; never used to broadcast here.</param>
    public AllocationManager(
        IValueAccessService valueAccess,
        IWalletManager walletManager,
        IWalletStore walletStore,
        IHolonStore holonStore,
        INftManager nftManager,
        IBlockchainOperationManager blockchainOps,
        IIdempotencyStore idempotencyStore,
        INodeFeeScheduleManager nodeFees,
        INodeGovernanceGuard? nodeGovernance = null,
        IUserStore? users = null,
        IPlatformReserve? reserve = null,
        ICustodialRecipientPreparer? recipientPreparer = null,
        INodeFeeSettlementManager? feeSettlement = null,
        INodeTreasuryManager? treasury = null,
        IBlockchainProviderFactory? providers = null,
        ReserveOptions? reserveOptions = null,
        ILogger<AllocationManager>? logger = null)
    {
        _valueAccess = valueAccess ?? throw new ArgumentNullException(nameof(valueAccess));
        _walletManager = walletManager ?? throw new ArgumentNullException(nameof(walletManager));
        _walletStore = walletStore ?? throw new ArgumentNullException(nameof(walletStore));
        _holonStore = holonStore ?? throw new ArgumentNullException(nameof(holonStore));
        _nftManager = nftManager ?? throw new ArgumentNullException(nameof(nftManager));
        _blockchainOps = blockchainOps ?? throw new ArgumentNullException(nameof(blockchainOps));
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _nodeGovernance = nodeGovernance ?? NodeGovernanceGuard.Unrestricted;
        _nodeFees = nodeFees ?? throw new ArgumentNullException(nameof(nodeFees));
        _users = users;
        _reserve = reserve;
        _recipientPreparer = recipientPreparer;
        _feeSettlement = feeSettlement;
        _treasury = treasury;
        _providers = providers;
        // The operator's platform-unit draw policy. The AUTHORITATIVE check runs at the
        // counter (PlatformReserveService.DrawAsync); this pre-check exists only so a
        // refusal precedes the recipient preparation it would otherwise pay for, and it
        // asks the very same predicate with the very same options.
        _reserveOptions = reserveOptions ?? new ReserveOptions();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<AllocationResult>> AllocateAsync(
        Guid userId,
        AllocationRequest request,
        Guid callerUserId,
        string? clientIdempotencyKey,
        string apiKeyId,
        Guid? actingTenantId = null,
        ReserveDrawAuthority? drawAuthority = null)
    {
        if (request is null)
            return AZOAResult<AllocationResult>.Failure("Allocation request is required.");
        if (string.IsNullOrWhiteSpace(request.ChainType))
            return AZOAResult<AllocationResult>.Failure("ChainType is required.");

        // On whose authority a reserve partition may be drawn. It DEFAULTS to the acting
        // tenant, so a caller that says nothing gets the narrow answer and a caller with no
        // resolvable tenant is refused at the counter (OI-21). Only a node-internal rail —
        // OnRampSettlementHandler — passes NodeOperator, and it does so positively.
        var authority = drawAuthority ?? ReserveDrawAuthority.ForTenant(actingTenantId);
        if (!Guid.TryParse(apiKeyId, out var callerApiKeyId) || callerApiKeyId == Guid.Empty)
            return AZOAResult<AllocationResult>.Failure("Caller API key context is invalid.");
        if (actingTenantId is { } tenantId)
        {
            if (tenantId != callerUserId || _users is null)
                return AZOAResult<AllocationResult>.Failure("Tenant allocation authority is unavailable.");
            var target = await _users.GetByIdAsync(userId);
            if (target.IsError || target.Result is null || target.Result.OwnerTenantId != tenantId)
                return AZOAResult<AllocationResult>.Failure(
                    TenantAuthorizationError.NotFound + "Custodial user not found.");
        }

        var governance = await _nodeGovernance.EnsureAllowedAsync(
            request.ChainType,
            ResolveGovernedAssetType(request),
            $"allocation:{request.Kind}");
        if (governance.IsError)
            return AZOAResult<AllocationResult>.Failure(governance.Message);

        // ── Step 1: idempotency key ───────────────────────────────────────────
        var allocationIdempotency = AllocationIdempotency.Create(
            callerApiKeyId, userId, request, clientIdempotencyKey);
        var ledgerKey = allocationIdempotency.LedgerKey;

        // ── Step 1b: quote-only ───────────────────────────────────────────────
        // A caller must be able to see the node fee BEFORE it locks terms with a
        // person. A preview runs every gate and every validation and then STOPS: no
        // idempotency claim, no wallet provisioning, no movement, empty operation id.
        // It deliberately sits before TryClaim so a preview can never consume the key
        // the real allocation will need.
        if (request.PreviewOnly)
            return await PreviewAsync(userId, request, actingTenantId);

        // ── Step 2: idempotency claim ─────────────────────────────────────────
        // TryClaim BEFORE any irreversible effect. On a lost claim we replay the
        // stored original result and perform NO second issue/transfer.
        var claim = await _idempotencyStore.TryClaimAsync(ledgerKey, OperationType, CancellationToken.None);
        if (!claim.Won)
            return ReplayFromRecord(claim.Record);

        var effectMayHaveStarted = false;
        try
        {
            // ── Step 3: KYC gate (fail-closed) ────────────────────────────────
            // Per D3, the value-bearing allocation is gated. Provisioning may
            // precede approval, but we gate before generating the wallet too so
            // a rejected user produces NO side effect at all under a won claim.
            var gate = await _valueAccess.RequireValueAccessAsync(userId, actingTenantId);
            if (gate.IsError)
            {
                await _idempotencyStore.FailAsync(ledgerKey, gate.Message, CancellationToken.None);
                return AZOAResult<AllocationResult>.Failure(gate.Message);
            }

            // ── Step 3b: is this kind admissible for this caller? ─────────────
            var admissible = ValidateKind(request, clientIdempotencyKey, actingTenantId);
            if (admissible is not null)
            {
                await _idempotencyStore.FailAsync(ledgerKey, admissible, CancellationToken.None);
                return AZOAResult<AllocationResult>.Failure(admissible);
            }

            // ── Step 4: parse the amount (H4) — reject BEFORE any broadcast ───
            // AllocationRequest.Amount is an arbitrary-precision string; the
            // provider value surface is ulong. A non-numeric / negative /
            // overflowing amount fails the idempotency key here so the claim is
            // terminal (Failed), never leaked InProgress, and nothing is broadcast.
            if (!TryParseAmount(request.Amount, out var amount, out var amountError))
            {
                await _idempotencyStore.FailAsync(ledgerKey, amountError, CancellationToken.None);
                return AZOAResult<AllocationResult>.Failure(amountError);
            }

            var feeQuoteResult = await QuoteAllocationFeeAsync(request, amount);
            if (feeQuoteResult.IsError || feeQuoteResult.Result is null)
            {
                var msg = feeQuoteResult.IsError ? feeQuoteResult.Message : "Node fee quote produced no result.";
                await _idempotencyStore.FailAsync(ledgerKey, msg, CancellationToken.None);
                return AZOAResult<AllocationResult>.Failure(msg);
            }
            var feeQuote = feeQuoteResult.Result;

            // ── Step 4b: a non-zero fee becomes a durable obligation ──────────
            // Shaving the fee off the moved amount without recording where it settles
            // is how a node quietly keeps money it never accounted for. Admitting the
            // obligation first makes the fee recoverable and auditable; a fee with
            // nowhere to settle still refuses.
            var feeObligation = await AdmitFeeObligationAsync(request, feeQuote, ledgerKey);
            if (feeObligation is not null)
            {
                await _idempotencyStore.FailAsync(ledgerKey, feeObligation, CancellationToken.None);
                return AZOAResult<AllocationResult>.Failure(feeObligation);
            }

            // ── Step 5: provision-if-absent ───────────────────────────────────
            var (wallet, provisioned, walletError) = await EnsureWalletAsync(userId, request.ChainType);
            if (wallet is null)
            {
                await _idempotencyStore.FailAsync(ledgerKey, walletError, CancellationToken.None);
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
                    userId, wallet, provisioned, request, feeQuote, ledgerKey,
                    allocationIdempotency.Correlation, callerUserId, callerApiKeyId, actingTenantId),
                AllocationKind.Transfer => await TransferAsync(
                    userId, callerUserId, wallet, provisioned, request, feeQuote,
                    ledgerKey, allocationIdempotency.Correlation, callerUserId, callerApiKeyId, actingTenantId),
                AllocationKind.IssueUnits => await IssueUnitsAsync(
                    userId, wallet, provisioned, request, feeQuote, ledgerKey,
                    allocationIdempotency.Correlation, callerUserId, callerApiKeyId, actingTenantId,
                    authority),
                _ => Operation.Invalid($"Unsupported allocation kind: {request.Kind}.")
            };

            if (opResult.IsError || opResult.Result is null)
            {
                var failure = ToConsumerSafeFailure(
                    opResult.IsError ? opResult.Message : "Allocation produced no operation.",
                    opResult.Code);
                await _idempotencyStore.FailAsync(ledgerKey, failure.Message, CancellationToken.None);
                return failure;
            }

            var op = opResult.Result;

            // ── Step 7: settle the alloc idempotency key from the op outcome ──
            // C2: complete the allocation idempotency key ONLY when the op carries a
            // real TxHash (a confirmed broadcast, or an M1 pending-with-hash). If the
            // op did not record a TxHash, leave the allocation record InProgress so a
            // redelivered request replays "in progress" — NOT a false success.
            var result = BuildAllocationResult(
                userId, wallet, provisioned, op.Id, feeQuote);

            var txHash = op.Parameters.GetValueOrDefault("TxHash", string.Empty);
            if (string.IsNullOrWhiteSpace(txHash))
            {
                // Broadcast did not (yet) record a settlement reference. Do NOT
                // Complete the key — leave it InProgress so reconciliation/replay can
                // settle it from chain truth. Surface a retryable in-progress state.
                return AZOAResult<AllocationResult>.FailureWithCode(
                    "This allocation is still settling and has not yet been confirmed. " +
                    "Retry once it settles; do not raise a second one.",
                    ConsumerSafeErrorCodes.SettlementPending);
            }

            await _idempotencyStore.CompleteAsync(
                ledgerKey, SerializeForReplay(result), CancellationToken.None);

            return new AZOAResult<AllocationResult> { Result = result, Message = "Allocation completed." };
        }
        catch (Exception ex)
        {
            if (!effectMayHaveStarted)
                await IdempotencyClaimRecovery.TryFailAsync(
                    _idempotencyStore, ledgerKey, UnexpectedFailureMessage, ex);
            throw;
        }
    }

    // ── Quote-only (terms before the lock) ────────────────────────────────────

    /// <summary>
    /// Runs every gate and validation an allocation runs, returns the exact gross / node
    /// fee / net / schedule version, and performs NOTHING. The result's
    /// <see cref="AllocationResult.OperationId"/> is <see cref="Guid.Empty"/>, which is
    /// the machine-checkable proof that no allocation happened.
    /// </summary>
    private async Task<AZOAResult<AllocationResult>> PreviewAsync(
        Guid userId, AllocationRequest request, Guid? actingTenantId)
    {
        var gate = await _valueAccess.RequireValueAccessAsync(userId, actingTenantId);
        if (gate.IsError)
            return AZOAResult<AllocationResult>.Failure(gate.Message);

        // A preview is exempt from the client-key requirement: it claims no key.
        var admissible = ValidateKind(request, "preview", actingTenantId);
        if (admissible is not null)
            return AZOAResult<AllocationResult>.Failure(admissible);

        if (!TryParseAmount(request.Amount, out var amount, out var amountError))
            return AZOAResult<AllocationResult>.Failure(amountError);

        var quote = await QuoteAllocationFeeAsync(request, amount);
        if (quote.IsError || quote.Result is null)
        {
            return AZOAResult<AllocationResult>.Failure(
                quote.IsError ? quote.Message : "Node fee quote produced no result.");
        }

        // A fee that could never settle would deny the real allocation, so the quote
        // must say so now rather than let a caller lock terms it cannot honour.
        var settlementRefusal = await DescribeFeeSettlementAvailabilityAsync(request, quote.Result);
        if (settlementRefusal is not null)
            return AZOAResult<AllocationResult>.Failure(settlementRefusal);

        var (wallet, _) = await FindWalletAsync(userId, request.ChainType);
        return new AZOAResult<AllocationResult>
        {
            Result = new AllocationResult
            {
                UserId = userId,
                WalletId = wallet?.Id ?? Guid.Empty,
                WalletAddress = wallet?.Address ?? string.Empty,
                WalletProvisioned = false,
                OperationId = Guid.Empty,
                Replayed = false,
                GrossAmount = FormatAmount(quote.Result.GrossAmount),
                NodeFeeAmount = FormatAmount(quote.Result.FeeAmount),
                NetAmount = FormatAmount(quote.Result.NetAmount),
                NodeFeeScheduleVersion = quote.Result.ScheduleVersion,
            },
            Message =
                "Quote only: no allocation was performed and no wallet was provisioned. " +
                "The empty operation id confirms it.",
        };
    }

    // ── Kind admissibility ────────────────────────────────────────────────────

    /// <summary>
    /// Refusal text when this kind cannot be run by this caller with these inputs, or null
    /// when it can. <see cref="AllocationKind.Mint"/> is retired for tenants (D47) and
    /// <see cref="AllocationKind.IssueUnits"/> demands the inputs an issuance cannot be
    /// safely derived without.
    /// </summary>
    private string? ValidateKind(
        AllocationRequest request, string? clientIdempotencyKey, Guid? actingTenantId)
    {
        switch (request.Kind)
        {
            case AllocationKind.Mint when actingTenantId.HasValue:
                return
                    "Creating supply per allocation is retired. Supply is created once into the " +
                    "platform reserve; use IssueUnits with the assetId and the partition the " +
                    "units are drawn from.";

            case AllocationKind.Mint:
                return null;

            // Transfer moves an asset the node already tracks, identified by
            // AssetRecordId; TransferAsync enforces that. AssetId is the optional
            // chain-native alias and is deliberately NOT made mandatory here.
            case AllocationKind.Transfer:
                return null;

            case AllocationKind.IssueUnits:
                if (_reserve is null || _recipientPreparer is null)
                {
                    return
                        "Issuing units is not available on this node: the platform reserve is not " +
                        "configured.";
                }
                if (string.IsNullOrWhiteSpace(request.AssetId))
                    return "AssetId is required to issue units: units are always units OF something.";
                if (!IsValidPartition(request.Partition))
                {
                    return
                        "Partition is required to issue units and must be 1-" + MaxPartitionLength +
                        " characters of letters, digits, '-' or '_'. Without it one purpose could " +
                        "quietly draw down another's earmark.";
                }
                if (request.Purpose is { Length: > MaxPurposeLength })
                    return $"Purpose must be {MaxPurposeLength} characters or fewer.";
                if (string.IsNullOrWhiteSpace(clientIdempotencyKey))
                {
                    return
                        "An Idempotency-Key is required to issue units. Two identical issuances are " +
                        "a normal thing to want, so the node will not guess whether a repeat is a " +
                        "retry or a second award.";
                }
                return null;

            default:
                return $"Unsupported allocation kind: {request.Kind}.";
        }
    }

    private static bool IsValidPartition(string? partition)
    {
        if (string.IsNullOrWhiteSpace(partition)) return false;
        var trimmed = partition.Trim();
        if (trimmed.Length > MaxPartitionLength) return false;
        foreach (var character in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')
                return false;
        }

        return true;
    }

    // ── Provision-if-absent ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the user's existing wallet for the chain, or generates one.
    /// Never duplicates (uniqueness is also guarded inside GenerateWalletAsync).
    /// </summary>
    private async Task<(IWallet? Wallet, bool Provisioned, string Error)> EnsureWalletAsync(
        Guid userId, string chainType)
    {
        var existing = await _walletStore.GetByUserAsync(userId);
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
            new WalletGenerateRequest { ChainType = chainType }, userId);
        if (gen.IsError || gen.Result is null)
            return (null, false, gen.IsError ? gen.Message : "Wallet provisioning failed.");

        return (gen.Result, true, string.Empty);
    }

    // ── Allocation execution (consumes existing surface verbatim) ──────────────

    private async Task<AZOAResult<IBlockchainOperation>> MintAsync(
        Guid userId, IWallet wallet, bool walletProvisioned, AllocationRequest request,
        AllocationFeeQuote feeQuote,
        string ledgerKey,
        string correlation,
        Guid callerUserId,
        Guid callerApiKeyId,
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
        var holonResult = await _holonStore.UpsertAsync(NftHolonFactory.Create(mint, userId));
        if (holonResult.IsError || holonResult.Result is null)
            return Operation.Invalid(holonResult.Message);

        // Step B: build a typed mint op and drive it through the REAL broadcast
        // path (C2) so the provider is called and a TxHash is recorded.
        var op = new BlockchainOperation
        {
            UserId = userId,
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
            IdempotencyKey = correlation,
            InitiatorUserId = callerUserId,
            InitiatorApiKeyId = callerApiKeyId,
            Parameters = BuildOpParameters(wallet, request, feeQuote, ledgerKey, holonResult.Result.Id.ToString())
        };
        op.Parameters[IdempotencyParameterNames.ResultPayload] = SerializeForReplay(BuildAllocationResult(
            userId, wallet, walletProvisioned, op.Id, feeQuote));

        return await _blockchainOps.ExecuteAsync(op);
    }

    private async Task<AZOAResult<IBlockchainOperation>> TransferAsync(
        Guid targetUserId,
        Guid sourceUserId,
        IWallet targetWallet,
        bool walletProvisioned,
        AllocationRequest request,
        AllocationFeeQuote feeQuote,
        string ledgerKey,
        string correlation,
        Guid callerUserId,
        Guid callerApiKeyId,
        Guid? actingTenantId = null)
    {
        if (request.AssetRecordId is null || request.AssetRecordId == Guid.Empty)
            return Operation.Invalid("Transfer allocation requires AssetRecordId.");
        if (sourceUserId == targetUserId)
            return Operation.Invalid("Transfer allocation source and target users must differ.");

        var (sourceWallet, sourceWalletError) = await FindWalletAsync(sourceUserId, request.ChainType);
        if (sourceWallet is null)
            return Operation.Invalid(sourceWalletError);

        // Step A: record the ownership move via NftManager (Holon upsert + IDOR
        // guard). The transfer target is the authorised user, never a body id.
        var transfer = new NftTransferRequest
        {
            TargetUserId = targetUserId,
            WalletId = targetWallet.Id,
            Memo = request.Memo
        };
        var holonResult = await _nftManager.TransferAsync(
            request.AssetRecordId.Value, transfer, sourceUserId);
        if (holonResult.IsError || holonResult.Result is null)
            return Operation.Invalid(holonResult.Message);
        var holonId = holonResult.Result.Parameters?.GetValueOrDefault("holonId");

        // Step B: build a typed transfer op and broadcast it (C2). The recipient is
        // the target user's custodial wallet address; the asset is request.AssetId.
        var op = new BlockchainOperation
        {
            UserId = sourceUserId,
            WalletId = sourceWallet.Id,
            OperationType = "Transfer",
            Status = OperationStatus.Pending,
            SourceHolonId = request.AssetRecordId,
            RecipientAddress = targetWallet.Address,
            // AC4: tenant-driven transfer signs with the USER's key — gate it.
            ActingTenantId = actingTenantId,
            SigningScope = actingTenantId.HasValue ? AzoaScopes.TransferSign : null,
            IdempotencyKey = correlation,
            InitiatorUserId = callerUserId,
            InitiatorApiKeyId = callerApiKeyId,
            Parameters = BuildOpParameters(
                sourceWallet, request, feeQuote, ledgerKey,
                holonId)
        };
        if (!string.IsNullOrWhiteSpace(request.AssetId))
            op.Parameters["SourceTokenId"] = request.AssetId!;
        op.Parameters[IdempotencyParameterNames.ResultPayload] = SerializeForReplay(BuildAllocationResult(
            targetUserId, targetWallet, walletProvisioned, op.Id, feeQuote));

        return await _blockchainOps.ExecuteAsync(op);
    }

    /// <summary>
    /// D47 issuance: units already exist in the platform reserve, so this draws them from
    /// a named partition, makes the recipient's account able to hold them, and moves the
    /// net amount out of the reserve. The node fee is not moved — it simply never leaves
    /// the reserve, which is why an issuance needs no separate treasury leg.
    /// <para>
    /// Ordering is deliberate. Preparation runs BEFORE the partition draw: a refused draw
    /// after a successful preparation costs the node only the account minimum it advanced
    /// (which the recipient keeps and can reuse), whereas a draw committed before a failed
    /// preparation would record units as issued that nobody received.
    /// </para>
    /// </summary>
    private async Task<AZOAResult<IBlockchainOperation>> IssueUnitsAsync(
        Guid userId,
        IWallet wallet,
        bool walletProvisioned,
        AllocationRequest request,
        AllocationFeeQuote feeQuote,
        string ledgerKey,
        string correlation,
        Guid callerUserId,
        Guid callerApiKeyId,
        Guid? actingTenantId,
        ReserveDrawAuthority authority)
    {
        // ValidateKind has already refused when either is absent; these are the
        // compiler's proof, not a second policy.
        if (_reserve is null || _recipientPreparer is null)
            return Operation.Invalid("Issuing units is not available on this node.");

        var assetId = request.AssetId!.Trim();
        var partition = request.Partition!.Trim();

        // Cross-tenant control, the same predicate the hold seam asks. BOTH the asset and
        // the partition arrive off the caller's body, so without this a tenant could name
        // another tenant's asset — or the node's own unit — and draw down a budget it never
        // declared. It runs FIRST: a refusal must not cost the recipient's account the
        // minimum that preparation advances.
        var tenantRefusal = await RefuseOutOfScopeDrawAsync(request.ChainType, assetId, authority);
        if (tenantRefusal is not null)
        {
            _logger?.LogWarning(
                "Issuance refused: partition '{Partition}' of asset {Asset} is not this caller's to draw.",
                partition, assetId);
            return RailRefusal(tenantRefusal);
        }

        var account = await _reserve.GetAccountAsync(request.ChainType);
        if (account.IsError || account.Result is null)
        {
            _logger?.LogWarning(
                "Issuance refused: the reserve account for {Chain} did not resolve ({Detail}).",
                request.ChainType, account.Message);
            return RailRefusal(account.Message);
        }

        var reserveAddress = account.Result.Address;
        var reserveContext = StampTenant(
            SigningContext.PlatformFor(request.ChainType), actingTenantId, userId);
        var recipientContext = StampTenant(
            SigningContext.ForUser(userId, wallet.Id).ForChain(request.ChainType), actingTenantId, userId);

        // Recipient preparation — the first issuance to a real person fails here without
        // it, with a rail error naming the asset and its acceptance step.
        var prepared = await _recipientPreparer.PrepareAsync(
            new CustodialRecipientPreparation(
                request.ChainType,
                assetId,
                wallet.Address,
                recipientContext,
                reserveAddress,
                reserveContext));
        if (prepared.IsError || prepared.Result is null)
            return RailRefusal(prepared.Message);

        // Acceptance submitted but unconfirmed is NOT ready: sending units into an
        // account whose acceptance has not landed is exactly the rejection this whole
        // path exists to prevent. Refuse and let the caller retry.
        if (prepared.Result.PendingConfirmation)
        {
            return Operation.InvalidWithCode(
                "The recipient's account is being made ready and has not finished settling. " +
                "Retry shortly; nothing was moved.",
                ConsumerSafeErrorCodes.SettlementPending);
        }

        // The node-side oversubscription check. A partition that would be overdrawn is
        // REFUSED here, before any value moves, and the draw replays safely on retry
        // because the allocation ledger key is its operation key.
        var draw = await _reserve.DrawAsync(new ReserveDrawRequest(
            request.ChainType,
            assetId,
            partition,
            ReserveDrawKind.Issue,
            new BigInteger(feeQuote.NetAmount),
            ledgerKey,
            authority));
        if (draw.IsError || draw.Result is null)
        {
            _logger?.LogWarning(
                "Issuance refused by the reserve partition '{Partition}' for asset {Asset}: {Detail}",
                partition, assetId, draw.Message);
            return RailRefusal(draw.Message);
        }

        // The move itself. UserId carries the recipient (the grantor a tenant-driven
        // signature is checked against) while WalletId stays null, which is what makes
        // BlockchainOperationManager.BuildSigningContext resolve the PLATFORM key — the
        // units leave the reserve, not the recipient's wallet.
        var op = new BlockchainOperation
        {
            UserId = userId,
            WalletId = null,
            OperationType = AzoaScopes.IssueUnitsOperationType,
            Status = OperationStatus.Pending,
            RecipientAddress = wallet.Address,
            ActingTenantId = actingTenantId,
            SigningScope = actingTenantId.HasValue ? AzoaScopes.UnitsIssue : null,
            IdempotencyKey = correlation,
            InitiatorUserId = callerUserId,
            InitiatorApiKeyId = callerApiKeyId,
            Parameters = BuildOpParameters(reserveAddress, request, feeQuote, ledgerKey, holonId: null),
        };
        op.Parameters["SourceTokenId"] = assetId;
        op.Parameters["ReserveAddress"] = reserveAddress;
        op.Parameters["ReservePartition"] = partition;
        op.Parameters["AllocationKind"] = nameof(AllocationKind.IssueUnits);
        op.Parameters[IdempotencyParameterNames.ResultPayload] = SerializeForReplay(BuildAllocationResult(
            userId, wallet, walletProvisioned, op.Id, feeQuote));

        return await _blockchainOps.ExecuteAsync(op);
    }

    /// <summary>
    /// Refusal text when this caller may not draw the asset's declared partitions, or null
    /// when it may. The reserve's own row carries the tenant that registered the asset
    /// (<c>FungibleTokenManager</c> stamps it at registration), so the answer is read from
    /// the ledger rather than trusted off the request.
    /// </summary>
    private async Task<string?> RefuseOutOfScopeDrawAsync(
        string chainType, string assetId, ReserveDrawAuthority authority)
    {
        var reserveLedger = await _reserve!.GetLedgerAsync(chainType, assetId);
        return ReservePartitionScope.Refuse(reserveLedger?.Result, authority, assetId, _reserveOptions);
    }

    /// <summary>
    /// Threads the acting tenant + grantor + the ISSUANCE scope onto a signing context so
    /// the custody seam runs the LIVE consent check before any decrypt. Used only by the
    /// issuance path, so the scope is units:issue and never transfer:sign. A user-driven or
    /// platform-internal issuance needs no grant and is returned unchanged.
    /// </summary>
    private static SigningContext StampTenant(
        SigningContext context, Guid? actingTenantId, Guid grantorUserId)
        => actingTenantId is { } tenantId && tenantId != Guid.Empty
            ? context.ActingAs(tenantId, AzoaScopes.UnitsIssue, grantorUserId)
            : context;

    private async Task<(IWallet? Wallet, string Error)> FindWalletAsync(Guid userId, string chainType)
    {
        var wallets = await _walletStore.GetByUserAsync(userId);
        if (wallets.IsError)
            return (null, wallets.Message);

        var wallet = wallets.Result?.FirstOrDefault(w =>
            string.Equals(w.ChainType, chainType, StringComparison.OrdinalIgnoreCase));
        return wallet is null
            ? (null, $"Source user has no {chainType} wallet for this transfer.")
            : (wallet, string.Empty);
    }

    /// <summary>
    /// Build the op Parameters carrying everything the broadcast + reconciliation
    /// need: the wallet address the provider signs/moves from, the asset
    /// descriptor, the arbitrary-precision <c>Amount</c> string (the value channel
    /// for op types with NO typed amount field — Transfer/Burn), the chain, and
    /// (H1) the raw internal allocation ledger key so an orphaned claim can be released by
    /// <c>ReconciliationService</c>. For Mint the typed <see cref="IMintOperation.Amount"/>
    /// ulong is authoritative; this string entry is the fallback for the typeless
    /// op types and storage rehydration.
    /// </summary>
    private Dictionary<string, string> BuildOpParameters(
        IWallet wallet,
        AllocationRequest request,
        AllocationFeeQuote feeQuote,
        string ledgerKey,
        string? holonId)
        => BuildOpParameters(wallet.Address, request, feeQuote, ledgerKey, holonId);

    private Dictionary<string, string> BuildOpParameters(
        string senderAddress,
        AllocationRequest request,
        AllocationFeeQuote feeQuote,
        string ledgerKey,
        string? holonId)
    {
        var p = new Dictionary<string, string>
        {
            ["WalletAddress"] = senderAddress,
            ["Amount"] = FormatAmount(feeQuote.NetAmount),
            ["GrossAmount"] = FormatAmount(feeQuote.GrossAmount),
            ["NodeFeeAmount"] = FormatAmount(feeQuote.FeeAmount),
            ["NetAmount"] = FormatAmount(feeQuote.NetAmount),
            ["NodeFeeScheduleVersion"] = feeQuote.ScheduleVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["AssetType"] = request.Name,
            ["ChainType"] = request.ChainType,
            // This parameter is internal reconciliation state. The durable operation
            // field holds only the opaque correlation safe for receipts and listings.
            ["IdempotencyKey"] = ledgerKey,
        };
        if (!string.IsNullOrWhiteSpace(request.AssetId))
            p["TokenUri"] = request.AssetId!;
        if (!string.IsNullOrWhiteSpace(holonId))
            p["holonId"] = holonId;

        // The caller's own words for WHY these units moved, carried onto the durable
        // operation. SurrealBlockchainOperationStore persists the whole parameters bag
        // onto operation_log.parameters, so this is what lands in the log.
        if (!string.IsNullOrWhiteSpace(request.Purpose))
            p["Purpose"] = request.Purpose!.Trim();

        // Never let an unstamped network silently pin a production allocation to devnet:
        // BlockchainOperationManager defaults the parameter to Devnet when it is absent.
        if (_providers is not null)
            p["ChainNetwork"] = _providers.DefaultNetwork.ToString();

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

    // ── Node fee: quote, pin, and settle ──────────────────────────────────────

    private async Task<AZOAResult<AllocationFeeQuote>> QuoteAllocationFeeAsync(
        AllocationRequest request,
        ulong grossAmount)
    {
        if (!TryResolveFeeOperation(request.Kind, out var operation))
            return AZOAResult<AllocationFeeQuote>.Failure($"Unsupported allocation kind: {request.Kind}.");

        var quote = await _nodeFees.QuoteAsync(operation, grossAmount);
        if (quote.IsError || quote.Result is null)
            return AZOAResult<AllocationFeeQuote>.Failure(
                quote.IsError ? quote.Message : "Node fee quote produced no result.");

        var converted = ToAllocationFeeQuote(quote.Result, grossAmount);
        if (converted.IsError || converted.Result is null)
            return converted;

        // Terms the caller quoted must be the terms it settles on. A schedule that has
        // moved since the quote is a REFUSAL, not a silent re-price.
        if (request.ExpectedFeeScheduleVersion is { } expected
            && expected != converted.Result.ScheduleVersion)
        {
            return AZOAResult<AllocationFeeQuote>.Failure(
                $"The node fee schedule has changed since this was quoted (quoted version {expected}, " +
                $"current version {converted.Result.ScheduleVersion}). Re-quote before allocating.");
        }

        return converted;
    }

    /// <summary>
    /// Admits a non-zero node fee as a durable, recoverable obligation, or returns the
    /// refusal. Returns null when there is nothing to admit.
    /// <para>
    /// An issuance needs no obligation: its fee never leaves the reserve, so there is no
    /// second effect to settle and nothing to strand. A transfer between two accounts is
    /// different — the fee has to reach the node's treasury from somewhere, so it is
    /// admitted here and refused outright when no destination is configured.
    /// </para>
    /// </summary>
    private async Task<string?> AdmitFeeObligationAsync(
        AllocationRequest request, AllocationFeeQuote feeQuote, string ledgerKey)
    {
        if (feeQuote.FeeAmount == 0 || request.Kind != AllocationKind.Transfer)
            return null;

        var destination = await ResolveTreasuryDestinationAsync(request.ChainType);
        if (destination.IsError || destination.Result is null)
            return destination.Message;

        if (_feeSettlement is null)
            return NoTreasurySettlementMessage;

        var admitted = await _feeSettlement.PrepareAsync(new NodeFeeSettlementDraft
        {
            ParentIdempotencyKey = ledgerKey,
            Operation = NodeFeeOperation.Transfer,
            Chain = destination.Result.Chain,
            Network = destination.Result.Network,
            AssetId = request.AssetId ?? string.Empty,
            GrossAmount = FormatAmount(feeQuote.GrossAmount),
            FeeAmount = FormatAmount(feeQuote.FeeAmount),
            NetAmount = FormatAmount(feeQuote.NetAmount),
            FeeScheduleVersion = feeQuote.ScheduleVersion,
            TreasuryAddress = destination.Result.Address,
            TreasuryDestinationVersion = destination.Result.Version,
        });

        return admitted.IsError || admitted.Result is null
            ? $"{NoTreasurySettlementMessage} ({ConsumerSafeErrors.Map(admitted.Message).Code})"
            : null;
    }

    /// <summary>Read-only twin of <see cref="AdmitFeeObligationAsync"/> for the quote path.</summary>
    private async Task<string?> DescribeFeeSettlementAvailabilityAsync(
        AllocationRequest request, AllocationFeeQuote feeQuote)
    {
        if (feeQuote.FeeAmount == 0 || request.Kind != AllocationKind.Transfer)
            return null;

        if (_feeSettlement is null)
            return NoTreasurySettlementMessage;

        var destination = await ResolveTreasuryDestinationAsync(request.ChainType);
        return destination.IsError || destination.Result is null ? destination.Message : null;
    }

    private async Task<AZOAResult<NodeTreasuryDestinationResponse>> ResolveTreasuryDestinationAsync(
        string chainType)
    {
        if (_treasury is null || _providers is null)
        {
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure(NoTreasurySettlementMessage);
        }

        var destination = await _treasury.GetDestinationAsync(chainType, _providers.DefaultNetwork);
        if (destination.IsError || destination.Result is null
            || string.IsNullOrWhiteSpace(destination.Result.Address))
        {
            _logger?.LogWarning(
                "Transfer fee refused: no treasury destination for {Chain} ({Detail}).",
                chainType, destination.Message);
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure(NoTreasurySettlementMessage);
        }

        return destination;
    }

    /// <summary>
    /// The refusal a transfer fee gets when it has nowhere to go. Kept as one constant so
    /// the wording — and the "treasury settlement" phrase callers and tests key on —
    /// cannot drift between the quote path and the allocation path.
    /// </summary>
    private const string NoTreasurySettlementMessage =
        "Node fees for this operation require a configured treasury settlement destination, " +
        "which this node does not have; allocation denied.";

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

    /// <summary>
    /// Maps an allocation kind onto the fee schedule entry that prices it. Issuance is
    /// priced as a transfer because that is exactly what it is on the rail — units moving
    /// out of the reserve — so it needs no schedule entry of its own.
    /// </summary>
    private static bool TryResolveFeeOperation(AllocationKind kind, out NodeFeeOperation operation)
    {
        switch (kind)
        {
            case AllocationKind.Mint:
                operation = NodeFeeOperation.Mint;
                return true;
            case AllocationKind.Transfer:
            case AllocationKind.IssueUnits:
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

    /// <summary>
    /// A refusal that originated on the rail (the reserve, the provider, the preparer).
    /// It is ALWAYS mapped: rail text is written for an operator and mentions accounts,
    /// assets and acceptance steps, none of which may reach a tenant.
    /// </summary>
    private static AZOAResult<IBlockchainOperation> RailRefusal(string? railMessage)
    {
        var mapped = ConsumerSafeErrors.Map(railMessage);
        return Operation.InvalidWithCode(mapped.Message, mapped.Code);
    }

    /// <summary>
    /// Last gate before a failure leaves for a tenant. A message carrying rail detail is
    /// REPLACED by its consumer-safe equivalent; one that does not is passed through so the
    /// node's own plain-language refusals keep their meaning. An already-assigned code
    /// (a rail refusal that mapped itself upstream) always wins.
    /// </summary>
    private static AZOAResult<AllocationResult> ToConsumerSafeFailure(string? message, string? code)
    {
        if (!ConsumerSafeErrors.LeaksRailDetail(message))
        {
            return code is null
                ? AZOAResult<AllocationResult>.Failure(message ?? string.Empty)
                : AZOAResult<AllocationResult>.FailureWithCode(message ?? string.Empty, code);
        }

        var mapped = ConsumerSafeErrors.Map(message);
        return AZOAResult<AllocationResult>.FailureWithCode(mapped.Message, code ?? mapped.Code);
    }

    private static AllocationResult BuildAllocationResult(
        Guid userId,
        IWallet wallet,
        bool walletProvisioned,
        Guid operationId,
        AllocationFeeQuote feeQuote)
        => new()
        {
            UserId = userId,
            WalletId = wallet.Id,
            WalletAddress = wallet.Address,
            WalletProvisioned = walletProvisioned,
            OperationId = operationId,
            Replayed = false,
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

    private static AZOAResult<AllocationResult> ReplayFromRecord(
        IdempotencyRecord record)
        => IdempotencyReplay.ReplayFromRecord<AllocationResult>(
            record,
            DeserializeForReplay,
            r => r.Replayed = true,
            "Duplicate request: returning the result of the original allocation (not re-executed).",
            "Duplicate request: original allocation result could not be replayed.",
            "Original allocation failed.",
            "An allocation is already being processed. Retry once the original request settles.");

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

        public static AZOAResult<IBlockchainOperation> InvalidWithCode(string message, string code)
            => new() { IsError = true, Message = message, Code = code };
    }
}
