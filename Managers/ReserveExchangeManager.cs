// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
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
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Instantiates the exchange bundle per request and settles it as one atomic group.
/// See <see cref="IReserveExchangeManager"/>.
/// </summary>
/// <remarks>
/// <para><b>Ordering is the safety property.</b> Everything that can refuse runs before
/// anything moves: shape, node governance, the rate policy, identity readiness, the reserve's
/// named-partition ledger and the project's own treasury availability. Only then is the group
/// submitted, and a submission that does not settle is COMPENSATED — the partition commitment
/// is handed back — so a refused exchange never strands a project's earmark.</para>
/// <para><b>The idempotency key is domain-separated from the allocation ledger.</b> Both live
/// in the same store, but an exchange key is <c>exchange:{apiKey}:{clientKey}</c> and an
/// allocation key is <c>alloc:{apiKey}:{clientKey}</c>. Sharing the derivation would let one
/// caller's allocation replay another operation's stored payload under the same client key.</para>
/// </remarks>
public sealed class ReserveExchangeManager : IReserveExchangeManager
{
    /// <summary>Idempotency ledger operation type. Deliberately not the allocation one.</summary>
    public const string OperationType = "reserve_exchange";

    /// <summary>The durable operation type every exchange is recorded under.</summary>
    public const string ChainOperationType = "Exchange";

    /// <summary>
    /// The scope an exchange signs under. It is the one <see cref="AzoaScopes"/> pairs with
    /// <see cref="ChainOperationType"/>, so the edge consent probe, the stored operation row and
    /// the custody seam all ask about the same capability.
    /// </summary>
    public const string ExchangeSigningScope = AzoaScopes.SwapSign;

    private const string ExchangeFailedCode = "EXCHANGE_FAILED";
    private const string UnexpectedFailureMessage = "The exchange failed unexpectedly.";
    private const string UnavailableMessage =
        "The exchange service is temporarily unavailable. Try again later.";
    private const int MaxProjectReferenceLength = 128;
    private const int MaxPartitionLength = 64;
    private const int MaxPurposeLength = 256;
    private const int MaxHoldReferenceLength = 96;

    private readonly IValueAccessService _valueAccess;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly INodeGovernanceGuard _nodeGovernance;
    private readonly IPlatformReserve _reserve;
    private readonly IProjectTreasury _treasury;
    private readonly IPlatformHold _holds;
    private readonly IPlatformTokenGenesis _platformToken;
    private readonly IWalletStore _wallets;
    private readonly ICustodialRecipientPreparer _recipientPreparer;
    private readonly IExchangeGroupSubmitter _groupSubmitter;
    private readonly IExchangeRatePolicy _ratePolicy;
    private readonly IExchangeDeclaredRateWriter _declaredRates;
    private readonly IBlockchainOperationStore _operationStore;
    private readonly IBlockchainProviderFactory _providers;
    private readonly ExchangeOptions _options;
    private readonly IValueRateSource _valueRateSource;
    private readonly ILogger<ReserveExchangeManager>? _logger;

    /// <param name="valueRateSource">
    /// Prices the leg the initiator gave up (D16). Defaults to the honest "never quotes"
    /// source so an unwired node records an exchange unpriced rather than mispriced.
    /// </param>
    public ReserveExchangeManager(
        IValueAccessService valueAccess,
        IIdempotencyStore idempotencyStore,
        IPlatformReserve reserve,
        IProjectTreasury treasury,
        IPlatformHold holds,
        IPlatformTokenGenesis platformToken,
        IWalletStore wallets,
        ICustodialRecipientPreparer recipientPreparer,
        IExchangeGroupSubmitter groupSubmitter,
        IExchangeRatePolicy ratePolicy,
        IExchangeDeclaredRateWriter declaredRates,
        IBlockchainOperationStore operationStore,
        IBlockchainProviderFactory providers,
        ExchangeOptions options,
        INodeGovernanceGuard? nodeGovernance = null,
        IValueRateSource? valueRateSource = null,
        ILogger<ReserveExchangeManager>? logger = null)
    {
        _valueAccess = valueAccess ?? throw new ArgumentNullException(nameof(valueAccess));
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _reserve = reserve ?? throw new ArgumentNullException(nameof(reserve));
        _treasury = treasury ?? throw new ArgumentNullException(nameof(treasury));
        _holds = holds ?? throw new ArgumentNullException(nameof(holds));
        _platformToken = platformToken ?? throw new ArgumentNullException(nameof(platformToken));
        _wallets = wallets ?? throw new ArgumentNullException(nameof(wallets));
        _recipientPreparer = recipientPreparer ?? throw new ArgumentNullException(nameof(recipientPreparer));
        _groupSubmitter = groupSubmitter ?? throw new ArgumentNullException(nameof(groupSubmitter));
        _ratePolicy = ratePolicy ?? throw new ArgumentNullException(nameof(ratePolicy));
        _declaredRates = declaredRates ?? throw new ArgumentNullException(nameof(declaredRates));
        _operationStore = operationStore ?? throw new ArgumentNullException(nameof(operationStore));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nodeGovernance = nodeGovernance ?? NodeGovernanceGuard.Unrestricted;
        _valueRateSource = valueRateSource ?? UnpricedValueRateSource.Instance;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ExchangeReceipt>> ExchangeAsync(
        ExchangeRequest request, ExchangeCallerContext caller, CancellationToken ct = default)
    {
        if (request is null || caller is null)
            return Invalid("An exchange request and its caller context are required.");

        // ── Step 1: shape ─────────────────────────────────────────────────────
        var shapeError = DescribeShapeRefusal(request, caller);
        if (shapeError is not null)
            return Invalid(shapeError);

        if (!TryParseAmount(request.PlatformAmount, out var platformAmount, out var amountError)
            || !TryParseAmount(request.ProjectUnitAmount, out var projectUnitAmount, out amountError))
        {
            return Invalid(amountError);
        }

        var chainType = request.ChainType.Trim();
        var projectAssetId = request.ProjectAssetId.Trim();
        var partition = request.ReservePartition.Trim();
        var projectReference = request.ProjectReference.Trim();
        var now = DateTimeOffset.UtcNow;

        // ── Step 2: node governance ───────────────────────────────────────────
        var governance = await _nodeGovernance.EnsureAllowedAsync(
            chainType, projectAssetId, $"exchange:{request.Direction}", ct);
        if (governance.IsError)
            return NotAllowed(governance.Message);

        // ── Step 3: the platform unit this node actually issues ───────────────
        // Read, never taken from the request: a caller that could name the funds asset could
        // pay a project in something the node never sold.
        var platformState = await _platformToken.GetStateAsync(ct);
        if (platformState.IsError || platformState.Result is null)
        {
            _logger?.LogWarning(
                "Exchange refused: the platform unit is not available ({Detail}).", platformState.Message);
            return Unavailable();
        }

        var platformAssetId = platformState.Result.Definition.AssetId;
        if (!string.Equals(platformState.Result.Definition.ChainType, chainType, StringComparison.OrdinalIgnoreCase))
        {
            return NotAllowed(
                "This node does not run its platform unit on the rail this exchange names.");
        }

        // ── Step 4: the declared rate, and the policy that admits it ──────────
        if (!DeclaredRateBinding.TryBind(
                request.DeclaredRate, chainType, projectAssetId, out var declaredRate, out var rateError))
        {
            return Invalid(rateError);
        }

        // The buy-back queue's replay anchor is this exchange's own idempotency identity, so a
        // retry re-reads its own place rather than taking a second one. Derived HERE — before the
        // rate policy runs — and reused for the ledger claim at step 7.
        var identity = ExchangeIdempotency.CreateFromClientKey(caller.ApiKeyId, caller.ClientIdempotencyKey);

        // The trailing five values are the buy-back context (W8.2). Without them the liquidity
        // policy cannot see the treasury a sell would be paid from and refuses every sell; and
        // without IsPreview a quote would take a place in the queue.
        var evaluation = new ExchangeRateEvaluation(
            chainType, request.Direction, projectAssetId, platformAssetId,
            platformAmount, projectUnitAmount, declaredRate, now,
            TenantId: caller.ActingTenantId,
            ProjectReference: projectReference,
            SellerUserId: caller.TargetUserId,
            RequestKey: identity.Correlation,
            IsPreview: request.PreviewOnly);
        var decision = await _ratePolicy.EvaluateAsync(evaluation, ct);
        if (decision.IsError || decision.Result is null)
        {
            return AZOAResult<ExchangeReceipt>.FailureWithCode(
                decision.Message, decision.Code ?? AzoaErrorCodes.OperationNotAllowed);
        }

        // ── Step 5: the hold window, if there is one ──────────────────────────
        var windowError = DescribeHoldWindowRefusal(request.ReleaseNotBefore, now);
        if (windowError is not null)
            return Invalid(windowError);

        // ── Step 6: quote-only ────────────────────────────────────────────────
        // A consumer must be able to run every gate BEFORE it locks terms with a person. A
        // preview claims no idempotency key, draws no partition and moves nothing; the absent
        // operation id is the machine-checkable proof.
        if (request.PreviewOnly)
        {
            var previewGate = await _valueAccess.RequireValueAccessAsync(
                caller.TargetUserId, caller.ActingTenantId, ct);
            if (previewGate.IsError)
                return AZOAResult<ExchangeReceipt>.Failure(previewGate.Message);

            return AZOAResult<ExchangeReceipt>.Success(
                BuildReceipt(
                    receiptReference: string.Empty,
                    operationId: null,
                    state: ExchangeReceiptState.AwaitingReconciliation,
                    request, caller, platformAssetId, platformAmount, projectUnitAmount,
                    decision.Result.DeclaredRate, submission: null, holdId: null,
                    projectCreditAmount: null, valueMinorUnits: null, createdAt: now.UtcDateTime),
                "Quote only: nothing was moved and no operation was created.");
        }

        // ── Step 7: idempotency claim, BEFORE any irreversible effect ─────────
        var ledgerKey = identity.LedgerKey;
        var claim = await _idempotencyStore.TryClaimAsync(ledgerKey, OperationType, CancellationToken.None);
        if (!claim.Won)
            return ReplayFromRecord(claim.Record);

        var effectMayHaveStarted = false;
        try
        {
            // ── Step 8: identity readiness (D46), fail closed ─────────────────
            var gate = await _valueAccess.RequireValueAccessAsync(
                caller.TargetUserId, caller.ActingTenantId, ct);
            if (gate.IsError)
                return await FailAndReturnAsync(ledgerKey, gate.Message, AzoaErrorCodes.Forbidden);

            // ── Step 9: record the rate this exchange was priced at ───────────
            // Written before the move so the value stamp read back afterwards is the SAME
            // figure that decided the quantity, not a second independently configured one.
            if (decision.Result.DeclaredRate is { } admittedRate)
            {
                var recorded = await _declaredRates.RecordAsync(admittedRate, ct);
                if (recorded.IsError)
                {
                    return await FailAndReturnAsync(
                        ledgerKey,
                        "The rate for this exchange could not be recorded, so it was not run.",
                        AzoaErrorCodes.DependencyUnavailable);
                }
            }

            // ── Step 10: the accounts ─────────────────────────────────────────
            var accounts = await ResolveAccountsAsync(caller, chainType, projectReference, ct);
            if (accounts.IsError || accounts.Result is null)
                return await FailAndReturnAsync(ledgerKey, accounts.Message, accounts.Code);

            var resolved = accounts.Result;
            // ExchangeSigningScope, not transfer:sign. AzoaScopes pairs the operation type
            // "Exchange" with swap:sign (the S6 guardrail map), so stamping anything else here
            // would make the edge consent probe and the custody seam disagree about what the
            // person consented to — and one of them would then be refusing the wrong thing.
            var recipientContext = SigningContext
                .ForUser(caller.TargetUserId, resolved.Wallet.Id)
                .ForChain(chainType)
                .ActingAs(caller.ActingTenantId, ExchangeSigningScope, caller.TargetUserId);
            var reserveContext = SigningContext
                .PlatformFor(chainType)
                .ActingAs(caller.ActingTenantId, ExchangeSigningScope, caller.TargetUserId);
            // The project's treasury is node-held but is NOT the reserve: its key is derived per
            // project, so a leg leaving it must be signed through IProjectTreasury rather than
            // with the chain's platform key. The leg carries that distinction as its own signer.
            var treasuryContext = SigningContext
                .PlatformFor(chainType)
                .ActingAs(caller.ActingTenantId, ExchangeSigningScope, caller.TargetUserId);

            // ── Step 11: make the recipient able to hold what they receive ────
            var takesDeliveryOf = request.Direction == ExchangeDirection.Buy
                ? (request.ReleaseNotBefore is null ? projectAssetId : null)
                : platformAssetId;
            if (takesDeliveryOf is not null)
            {
                var prepared = await _recipientPreparer.PrepareAsync(
                    new CustodialRecipientPreparation(
                        chainType, takesDeliveryOf, resolved.Wallet.Address, recipientContext,
                        resolved.ReserveAddress, reserveContext),
                    ct);
                if (prepared.IsError || prepared.Result is null)
                    return await FailAndReturnRailAsync(ledgerKey, prepared.Message);

                // Submitted-but-unconfirmed is NOT ready: sending units into an account whose
                // acceptance has not landed is the rejection this step exists to prevent.
                if (prepared.Result.PendingConfirmation)
                {
                    return await FailAndReturnAsync(
                        ledgerKey,
                        "This account is being made ready and has not finished settling. Retry "
                        + "shortly; nothing was moved.",
                        ConsumerSafeErrorCodes.SettlementPending);
                }
            }

            // ── Step 12: the node-side commitments that can refuse ────────────
            // The reserve's named partition and the project's own treasury both get their
            // answer BEFORE anything settles, so an oversubscribed earmark or a treasury with
            // nothing in it refuses here rather than after a sponsor has paid.
            var committed = await CommitAsync(
                request, caller, chainType, projectAssetId, platformAssetId, partition,
                projectReference, projectUnitAmount, platformAmount, ledgerKey, identity.Correlation, ct);
            if (committed.IsError || committed.Result is null)
                return await FailAndReturnAsync(ledgerKey, committed.Message, committed.Code);

            var commitment = committed.Result;

            // ── Step 13: instantiate the bundle ───────────────────────────────
            var bundle = ExchangeBundleDefinition.Compose(new ExchangeBundleInputs(
                identity.Correlation,
                ledgerKey,
                chainType,
                _providers.DefaultNetwork,
                request.Direction,
                caller.TargetUserId,
                projectReference,
                projectAssetId,
                platformAssetId,
                platformAmount,
                projectUnitAmount,
                partition,
                resolved.ReserveAddress,
                resolved.TreasuryAddress,
                resolved.Wallet.Address,
                recipientContext,
                reserveContext,
                treasuryContext,
                commitment.HoldId,
                request.ReleaseNotBefore,
                request.Purpose?.Trim(),
                decision.Result.PolicyVersion,
                decision.Result.DeclaredRate));
            if (bundle.IsError || bundle.Result is null)
            {
                await CompensateAsync(commitment, chainType, projectAssetId, partition, projectReference,
                    caller, projectUnitAmount, platformAmount, ledgerKey, ct);
                return await FailAndReturnAsync(ledgerKey, bundle.Message, bundle.Code);
            }

            // ── Step 14: settle the whole group, or nothing ───────────────────
            effectMayHaveStarted = true;
            var submission = await _groupSubmitter.SubmitAsync(bundle.Result.Group, ct);
            if (submission.IsError || submission.Result is null)
            {
                await CompensateAsync(commitment, chainType, projectAssetId, partition, projectReference,
                    caller, projectUnitAmount, platformAmount, ledgerKey, ct);
                return await FailAndReturnRailAsync(ledgerKey, submission.Message, submission.Code);
            }

            // ── Step 15: record what the project actually received ────────────
            string? projectCredit = null;
            if (request.Direction == ExchangeDirection.Buy)
            {
                var credited = await _treasury.RecordMovementAsync(
                    new ProjectTreasuryMovementRequest(
                        new ProjectTreasuryReference(chainType, caller.ActingTenantId, projectReference),
                        platformAssetId,
                        ProjectTreasuryMovementKind.Credit,
                        platformAmount,
                        ledgerKey),
                    ct);
                if (credited.IsError)
                {
                    // The funds are already the project's on chain. Refusing the exchange now
                    // would tell the consumer nothing happened while the money has moved, so
                    // the key stays InProgress and reconciliation settles it from chain truth.
                    _logger?.LogError(
                        "Exchange {Receipt} settled on chain but the project credit was not recorded: {Detail}",
                        identity.Correlation, credited.Message);
                    return AZOAResult<ExchangeReceipt>.FailureWithCode(
                        "This exchange has settled and is still being recorded. Do not raise a second one.",
                        ConsumerSafeErrorCodes.SettlementPending);
                }

                projectCredit = platformAmount.ToString(CultureInfo.InvariantCulture);
            }

            // ── Step 16: the durable record, stamped with what it was worth ───
            var settledAt = DateTimeOffset.UtcNow;
            var valueStamp = await StampValueAsync(
                request.Direction, chainType, platformAssetId, projectAssetId,
                platformAmount, projectUnitAmount, settledAt);

            var operation = BuildOperation(
                request, caller, resolved, chainType, platformAssetId, projectAssetId, partition,
                projectReference, platformAmount, projectUnitAmount, ledgerKey, identity.Correlation,
                bundle.Result, submission.Result, decision.Result, valueStamp, settledAt);

            var persisted = await _operationStore.UpsertAsync(operation, ct);
            if (persisted.IsError)
            {
                _logger?.LogError(
                    "Exchange {Receipt} settled on chain but its operation row was not written: {Detail}",
                    identity.Correlation, persisted.Message);
                return AZOAResult<ExchangeReceipt>.FailureWithCode(
                    "This exchange has settled and is still being recorded. Do not raise a second one.",
                    ConsumerSafeErrorCodes.SettlementPending);
            }

            var receipt = BuildReceipt(
                identity.Correlation,
                operation.Id,
                submission.Result.Confirmed
                    ? ExchangeReceiptState.Completed
                    : ExchangeReceiptState.AwaitingReconciliation,
                request, caller, platformAssetId, platformAmount, projectUnitAmount,
                decision.Result.DeclaredRate, submission.Result, commitment.HoldId, projectCredit,
                valueStamp?.MinorUnits, settledAt.UtcDateTime);

            // A group the rail accepted but has not confirmed is NOT terminal: completing the
            // key here would make a redelivered request replay a success the chain has not
            // agreed to yet.
            if (!submission.Result.Confirmed)
            {
                return AZOAResult<ExchangeReceipt>.FailureWithCode(
                    "This exchange is still settling and has not been confirmed. Retry once it "
                    + "settles; do not raise a second one.",
                    ConsumerSafeErrorCodes.SettlementPending);
            }

            await _idempotencyStore.CompleteAsync(
                ledgerKey, IdempotencyReplay.SerializeForReplay(receipt), CancellationToken.None);

            return AZOAResult<ExchangeReceipt>.Success(receipt, "Exchange completed.");
        }
        catch (Exception exception)
        {
            if (!effectMayHaveStarted)
            {
                await IdempotencyClaimRecovery.TryFailAsync(
                    _idempotencyStore, ledgerKey, UnexpectedFailureMessage, exception);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ExchangeReceipt>> GetReceiptAsync(
        ExchangeReceiptQuery query, CancellationToken ct = default)
    {
        if (query is null
            || query.ApiKeyId == Guid.Empty
            || query.CallerUserId == Guid.Empty
            || string.IsNullOrWhiteSpace(query.ClientIdempotencyKey))
        {
            return Invalid("A caller API key, caller identity and idempotency key are required.");
        }

        var identity = ExchangeIdempotency.CreateFromClientKey(query.ApiKeyId, query.ClientIdempotencyKey);

        IdempotencyRecord? ledger;
        try
        {
            ledger = await _idempotencyStore.GetAsync(identity.LedgerKey, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable();
        }

        // An allocation ledger entry under a coincidentally equal key is NOT this receipt.
        if (ledger is null
            || !string.Equals(ledger.OperationType, OperationType, StringComparison.Ordinal))
        {
            return NotFound();
        }

        IBlockchainOperation? operation = null;
        try
        {
            var found = await _operationStore.GetByIdempotencyKeyAsync(identity.Correlation, ct);
            if (found.Exception is not null)
                return Unavailable();
            if (found.IsError && !IsAbsentOperation(found))
                return Unavailable();
            if (!found.IsError && found.Result is null)
                return Unavailable();

            if (!found.IsError && found.Result is not null)
            {
                operation = found.Result;

                // Caller scope: the receipt belongs to the API key AND the identity that ran
                // it. A different key holder never learns that this exchange exists.
                if (operation.InitiatorApiKeyId != query.ApiKeyId
                    || operation.InitiatorUserId != query.CallerUserId
                    || !string.Equals(operation.IdempotencyKey, identity.Correlation, StringComparison.Ordinal))
                {
                    return NotFound();
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable();
        }

        var stored = string.IsNullOrWhiteSpace(ledger.ResultPayload)
            ? null
            : IdempotencyReplay.DeserializeForReplay<ExchangeReceipt>(ledger.ResultPayload!);

        var state = ResolveState(ledger.State, operation?.Status);
        var receipt = stored ?? new ExchangeReceipt { ReceiptReference = identity.Correlation };
        receipt.ReceiptReference = identity.Correlation;
        receipt.OperationId = operation?.Id ?? receipt.OperationId;
        receipt.State = state;
        receipt.IsTerminal = state != ExchangeReceiptState.AwaitingReconciliation;
        receipt.RequiresReconciliation = state == ExchangeReceiptState.AwaitingReconciliation;
        receipt.Replayed = false;
        receipt.CreatedAt = ledger.CreatedAt;
        receipt.UpdatedAt = operation?.CompletedDate is { } completedAt && completedAt > ledger.UpdatedAt
            ? completedAt
            : ledger.UpdatedAt;
        receipt.FailureCode = state == ExchangeReceiptState.Failed ? ExchangeFailedCode : null;

        return AZOAResult<ExchangeReceipt>.Success(receipt, "Exchange receipt found.");
    }

    // ── Shape ─────────────────────────────────────────────────────────────────

    /// <summary>Why this request cannot be run at all, or null when it can.</summary>
    private static string? DescribeShapeRefusal(ExchangeRequest request, ExchangeCallerContext caller)
    {
        if (caller.ApiKeyId == Guid.Empty || caller.CallerUserId == Guid.Empty)
            return "The exchange caller context is incomplete.";
        if (caller.TargetUserId == Guid.Empty)
            return "An exchange must name the person it is for.";
        if (caller.ActingTenantId == Guid.Empty)
            return "An exchange must be driven by a resolvable tenant.";
        if (string.IsNullOrWhiteSpace(caller.ClientIdempotencyKey))
        {
            return
                "An Idempotency-Key is required. Two identical sponsorships are a normal thing to "
                + "want, so the node will not guess whether a repeat is a retry or a second one.";
        }

        if (string.IsNullOrWhiteSpace(request.ChainType))
            return "An exchange must name the rail it settles on.";
        if (string.IsNullOrWhiteSpace(request.ProjectReference)
            || request.ProjectReference.Trim().Length > MaxProjectReferenceLength)
        {
            return $"A project reference is required and must be {MaxProjectReferenceLength} characters or fewer.";
        }

        if (string.IsNullOrWhiteSpace(request.ProjectAssetId))
            return "An exchange must name the project's units.";
        if (!IsValidPartition(request.ReservePartition))
        {
            return
                "A reserve partition is required and must be 1-" + MaxPartitionLength + " characters of "
                + "letters, digits, '-' or '_'. Without it one purpose could quietly draw down another's earmark.";
        }

        if (request.Purpose is { Length: > MaxPurposeLength })
            return $"Purpose must be {MaxPurposeLength} characters or fewer.";
        if (request.HoldReference is { } holdReference
            && (holdReference.Trim().Length == 0 || holdReference.Trim().Length > MaxHoldReferenceLength))
        {
            return $"A supplied hold reference must be 1-{MaxHoldReferenceLength} characters.";
        }

        return request.Direction is ExchangeDirection.Buy or ExchangeDirection.Sell
            ? null
            : $"Unsupported exchange direction: {request.Direction}.";
    }

    private string? DescribeHoldWindowRefusal(DateTimeOffset? releaseNotBefore, DateTimeOffset now)
    {
        if (releaseNotBefore is not { } window)
            return null;
        if (window <= now)
            return "A hold window must end in the future; use no window to take delivery now.";

        return window - now > _options.MaxHoldWindow
            ? "The requested hold window is longer than this node allows."
            : null;
    }

    private static bool IsValidPartition(string? partition)
    {
        if (string.IsNullOrWhiteSpace(partition))
            return false;

        var trimmed = partition.Trim();
        if (trimmed.Length > MaxPartitionLength)
            return false;

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Parses a whole base-unit amount. Rejects non-numeric, negative, fractional and zero
    /// values BEFORE any movement, so a bad amount never reaches a rail.
    /// </summary>
    private static bool TryParseAmount(string? raw, out BigInteger amount, out string error)
    {
        amount = BigInteger.Zero;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Both sides of an exchange must carry an amount.";
            return false;
        }

        if (!BigInteger.TryParse(
                raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out amount))
        {
            error = "An exchange amount must be a whole, non-negative number of base units.";
            return false;
        }

        if (amount <= BigInteger.Zero)
        {
            error = "An exchange amount must be positive.";
            return false;
        }

        return true;
    }

    // ── Accounts ──────────────────────────────────────────────────────────────

    /// <summary>The three accounts an exchange touches, or the refusal that stopped it.</summary>
    private async Task<AZOAResult<ResolvedAccounts>> ResolveAccountsAsync(
        ExchangeCallerContext caller,
        string chainType,
        string projectReference,
        CancellationToken ct)
    {
        var wallets = await _wallets.GetByUserAsync(caller.TargetUserId, ct);
        if (wallets.IsError)
            return AZOAResult<ResolvedAccounts>.FailureWithCode(
                UnavailableMessage, AzoaErrorCodes.DependencyUnavailable);

        var wallet = wallets.Result?.FirstOrDefault(candidate =>
            string.Equals(candidate.ChainType, chainType, StringComparison.OrdinalIgnoreCase));
        if (wallet is null)
        {
            // Deliberately NOT provision-if-absent. An account that has never existed holds no
            // funds, so a buy would fail on the funds leg anyway, and provisioning here would
            // create an account as a side effect of a request that cannot succeed.
            return AZOAResult<ResolvedAccounts>.FailureWithCode(
                "This person has no account on this rail yet, so there is nothing to exchange.",
                AzoaErrorCodes.InvalidRequest);
        }

        var reserve = await _reserve.GetAccountAsync(chainType, ct);
        if (reserve.IsError || reserve.Result is null)
        {
            _logger?.LogWarning(
                "Exchange refused: the reserve account for {Chain} did not resolve ({Detail}).",
                chainType, reserve.Message);
            return RailRefusal<ResolvedAccounts>(reserve.Message);
        }

        // Resolve-or-create: the project must have somewhere for the funds to land before
        // anyone can fund it, and a treasury is not a person, so no KYC gate applies to it.
        var treasury = await _treasury.ResolveAsync(
            new ProjectTreasuryReference(chainType, caller.ActingTenantId, projectReference), ct);
        if (treasury.IsError || treasury.Result is null)
        {
            _logger?.LogWarning(
                "Exchange refused: the treasury for project {Project} did not resolve ({Detail}).",
                projectReference, treasury.Message);
            return RailRefusal<ResolvedAccounts>(treasury.Message);
        }

        if (treasury.Result.Status != ProjectTreasuryStatus.Open)
        {
            return AZOAResult<ResolvedAccounts>.FailureWithCode(
                "This project is no longer accepting funds.", AzoaErrorCodes.OperationNotAllowed);
        }

        return AZOAResult<ResolvedAccounts>.Success(
            new ResolvedAccounts(wallet, reserve.Result.Address, treasury.Result.Address));
    }

    // ── Commitments and their compensation ────────────────────────────────────

    /// <summary>
    /// Takes every node-side commitment the exchange needs before it settles: the reserve's
    /// named partition (or a hold drawn from it) and, when the node is paying out, the
    /// project treasury's availability.
    /// </summary>
    private async Task<AZOAResult<ExchangeCommitment>> CommitAsync(
        ExchangeRequest request,
        ExchangeCallerContext caller,
        string chainType,
        string projectAssetId,
        string platformAssetId,
        string partition,
        string projectReference,
        BigInteger projectUnitAmount,
        BigInteger platformAmount,
        string ledgerKey,
        string correlation,
        CancellationToken ct)
    {
        if (request.Direction == ExchangeDirection.Sell)
        {
            // The node is paying the platform unit OUT of the project's own treasury, so the
            // treasury's availability is the control that must run first.
            var spend = await _treasury.RecordMovementAsync(
                new ProjectTreasuryMovementRequest(
                    new ProjectTreasuryReference(chainType, caller.ActingTenantId, projectReference),
                    platformAssetId,
                    ProjectTreasuryMovementKind.Spend,
                    platformAmount,
                    ledgerKey),
                ct);
            if (spend.IsError)
                return RailRefusal<ExchangeCommitment>(spend.Message);

            // Units coming back restore the partition's availability.
            var returned = await _reserve.DrawAsync(
                new ReserveDrawRequest(
                    chainType, projectAssetId, partition, ReserveDrawKind.Return,
                    projectUnitAmount, ledgerKey),
                ct);
            if (returned.IsError)
            {
                await CompensateTreasuryAsync(
                    chainType, caller, projectReference, platformAssetId, platformAmount, ledgerKey, ct);
                return RailRefusal<ExchangeCommitment>(returned.Message);
            }

            return AZOAResult<ExchangeCommitment>.Success(
                new ExchangeCommitment(
                    ExchangeCommitmentKind.PartitionReturn, null, true, platformAssetId));
        }

        if (request.ReleaseNotBefore is { } releaseNotBefore)
        {
            // A hold is the units leg when the sponsor's stake is not theirs to move yet. The
            // hold primitive draws the partition itself, so the manager must not draw twice.
            var holdId = string.IsNullOrWhiteSpace(request.HoldReference)
                ? $"exchange:{correlation}"
                : request.HoldReference!.Trim();

            var opened = await _holds.OpenAsync(
                new HoldOpenRequest(
                    new HoldReference(chainType, holdId),
                    projectAssetId,
                    projectUnitAmount,
                    HoldFundingSource.Reserve,
                    caller.TargetUserId,
                    partition,
                    FunderUserId: default,
                    ReleaseNotBefore: releaseNotBefore,
                    AutoRelease: true,
                    ActingTenantId: caller.ActingTenantId,
                    Purpose: request.Purpose?.Trim()),
                ct);
            if (opened.IsError || opened.Result is null)
                return RailRefusal<ExchangeCommitment>(opened.Message);

            return AZOAResult<ExchangeCommitment>.Success(
                new ExchangeCommitment(ExchangeCommitmentKind.Hold, holdId, false));
        }

        // Straight delivery: the node-side oversubscription check, before any value moves.
        var issued = await _reserve.DrawAsync(
            new ReserveDrawRequest(
                chainType, projectAssetId, partition, ReserveDrawKind.Issue,
                projectUnitAmount, ledgerKey),
            ct);
        if (issued.IsError)
        {
            _logger?.LogWarning(
                "Exchange refused by the reserve partition '{Partition}' for asset {Asset}: {Detail}",
                partition, projectAssetId, issued.Message);
            return RailRefusal<ExchangeCommitment>(issued.Message);
        }

        return AZOAResult<ExchangeCommitment>.Success(
            new ExchangeCommitment(ExchangeCommitmentKind.PartitionIssue, null, false));
    }

    /// <summary>
    /// Hands back every commitment a failed settlement left standing. Without it a refused
    /// exchange would keep a project's earmark permanently spoken for.
    /// </summary>
    private async Task CompensateAsync(
        ExchangeCommitment commitment,
        string chainType,
        string projectAssetId,
        string partition,
        string projectReference,
        ExchangeCallerContext caller,
        BigInteger projectUnitAmount,
        BigInteger platformAmount,
        string ledgerKey,
        CancellationToken ct)
    {
        try
        {
            switch (commitment.Kind)
            {
                case ExchangeCommitmentKind.Hold when commitment.HoldId is { } holdId:
                    await _holds.RefundAsync(
                        new HoldSettlementRequest(
                            new HoldReference(chainType, holdId),
                            caller.ActingTenantId,
                            "The exchange did not settle."),
                        ct);
                    break;

                case ExchangeCommitmentKind.PartitionIssue:
                    await _reserve.DrawAsync(
                        new ReserveDrawRequest(
                            chainType, projectAssetId, partition, ReserveDrawKind.Return,
                            projectUnitAmount, ledgerKey + ":compensate"),
                        ct);
                    break;

                case ExchangeCommitmentKind.PartitionReturn:
                    await _reserve.DrawAsync(
                        new ReserveDrawRequest(
                            chainType, projectAssetId, partition, ReserveDrawKind.Issue,
                            projectUnitAmount, ledgerKey + ":compensate"),
                        ct);
                    break;
            }

            if (commitment.TreasurySpent)
            {
                await CompensateTreasuryAsync(
                    chainType, caller, projectReference, PlatformAssetOf(commitment), platformAmount,
                    ledgerKey, ct);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed compensation must never mask the failure that caused it. It is logged
            // loudly so an operator can hand the commitment back by hand.
            _logger?.LogError(
                exception,
                "Could not release the commitment left by a failed exchange on {Chain}/{Asset} "
                + "partition '{Partition}'. It needs an operator.",
                chainType, projectAssetId, partition);
        }
    }

    /// <summary>Puts a treasury spend back when the settlement it was taken for did not happen.</summary>
    private async Task CompensateTreasuryAsync(
        string chainType,
        ExchangeCallerContext caller,
        string projectReference,
        string platformAssetId,
        BigInteger platformAmount,
        string ledgerKey,
        CancellationToken ct)
        => await _treasury.RecordMovementAsync(
            new ProjectTreasuryMovementRequest(
                new ProjectTreasuryReference(chainType, caller.ActingTenantId, projectReference),
                platformAssetId,
                ProjectTreasuryMovementKind.Credit,
                platformAmount,
                ledgerKey + ":compensate"),
            ct);

    /// <summary>The asset a treasury spend was taken in; only a sell ever spends.</summary>
    private static string PlatformAssetOf(ExchangeCommitment commitment) =>
        commitment.PlatformAssetId ?? string.Empty;

    // ── Value stamp (D16) ─────────────────────────────────────────────────────

    /// <summary>
    /// Prices the leg the initiator GAVE UP, dimensioned by the asset it is counted in. A
    /// source that declines or throws leaves the row unpriced; nothing is ever substituted.
    /// </summary>
    private async Task<ValueStamp?> StampValueAsync(
        ExchangeDirection direction,
        string chainType,
        string platformAssetId,
        string projectAssetId,
        BigInteger platformAmount,
        BigInteger projectUnitAmount,
        DateTimeOffset settledAt)
    {
        var (assetId, baseUnits) = direction == ExchangeDirection.Buy
            ? (platformAssetId, platformAmount)
            : (projectAssetId, projectUnitAmount);

        var dimension = ValueDimension.ChainAsset(assetId);
        var amount = RailAmount.In(dimension, baseUnits);

        try
        {
            var quote = await _valueRateSource.TryQuoteAsync(
                dimension, chainType, settledAt, CancellationToken.None);
            return quote is null ? null : ValueStamp.From(amount, quote);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "The value rate source failed for a settled exchange on {Chain}; recording it "
                + "unpriced rather than stamping a guessed value.",
                chainType);
            return null;
        }
    }

    // ── Durable record ────────────────────────────────────────────────────────

    private BlockchainOperation BuildOperation(
        ExchangeRequest request,
        ExchangeCallerContext caller,
        ResolvedAccounts accounts,
        string chainType,
        string platformAssetId,
        string projectAssetId,
        string partition,
        string projectReference,
        BigInteger platformAmount,
        BigInteger projectUnitAmount,
        string ledgerKey,
        string correlation,
        ExchangeBundle bundle,
        ExchangeGroupSubmission submission,
        ExchangeRateDecision decision,
        ValueStamp? valueStamp,
        DateTimeOffset settledAt)
    {
        var buying = request.Direction == ExchangeDirection.Buy;
        var spentAssetId = buying ? platformAssetId : projectAssetId;
        var spentAmount = buying ? platformAmount : projectUnitAmount;
        var receivedAssetId = buying ? projectAssetId : platformAssetId;
        var receivedAmount = buying ? projectUnitAmount : platformAmount;

        var operation = new BlockchainOperation
        {
            Id = bundle.Bundle.Id,
            UserId = caller.TargetUserId,
            WalletId = accounts.Wallet.Id,
            OperationType = ChainOperationType,
            // A group the rail ACCEPTED but has not CONFIRMED is not an exchange that happened.
            // Recording it as Exchanged would put a claim on the row that chain truth has not
            // agreed to, and the receipt reads this status to decide whether it is terminal.
            Status = submission.Confirmed
                ? OperationStatus.Exchanged
                : OperationStatus.PendingConfirmation,
            ActingTenantId = caller.ActingTenantId,
            SigningScope = ExchangeSigningScope,
            IdempotencyKey = correlation,
            InitiatorUserId = caller.CallerUserId,
            InitiatorApiKeyId = caller.ApiKeyId,
            // The typed amount and the parameter below carry the SAME leg — the one given up —
            // so the value stamp and the row can never disagree about what moved.
            Amount = RailAmount.In(ValueDimension.ChainAsset(spentAssetId), spentAmount),
            ExchangeRate = decision.DeclaredRate is { } rate
                ? $"{rate.MinorUnitsPerBaseUnit.ToString(CultureInfo.InvariantCulture)} {rate.CurrencyCode}"
                : null,
            ValueStamp = valueStamp,
            CompletedDate = submission.Confirmed ? settledAt.UtcDateTime : null,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WalletAddress"] = accounts.Wallet.Address,
                ["ChainType"] = chainType,
                ["ChainNetwork"] = _providers.DefaultNetwork.ToString(),
                ["Amount"] = spentAmount.ToString(CultureInfo.InvariantCulture),
                ["SourceTokenId"] = spentAssetId,
                ["TargetTokenId"] = receivedAssetId,
                ["AmountOut"] = receivedAmount.ToString(CultureInfo.InvariantCulture),
                ["ExchangeDirection"] = request.Direction.ToString(),
                ["ProjectReference"] = projectReference,
                ["ProjectTreasuryAddress"] = accounts.TreasuryAddress,
                ["ReserveAddress"] = accounts.ReserveAddress,
                ["ReservePartition"] = partition,
                ["ExchangePolicyVersion"] = decision.PolicyVersion,
                ["ExchangeGroupIdentity"] = bundle.Group.GroupIdentity,
                ["ExchangeGroupReference"] = submission.GroupReference,
                ["ExchangeGroupLegReferences"] = string.Join(',', submission.LegReferences),
                ["ExchangeBundleDeployment"] = bundle.Bundle.DeploymentConfig ?? string.Empty,
                // Internal reconciliation state; the durable field above holds only the opaque
                // correlation that is safe to return in a receipt.
                ["IdempotencyKey"] = ledgerKey,
            },
        };

        // A reconciler observes a TRANSACTION, not a group: a group reference is not something
        // GetTransactionConfirmationAsync can look up. The first leg is the transaction that
        // stands for the whole group — they land together or not at all — so that is what goes
        // under the name every other operation type uses. The group reference keeps its own key.
        operation.Parameters["TxHash"] = submission.LegReferences.Count > 0
            ? submission.LegReferences[0]
            : submission.GroupReference;

        if (decision.DeclaredRate is { } declared)
        {
            operation.Parameters["ExchangeRateSource"] = declared.Source;
            operation.Parameters["ExchangeRateAsOf"] =
                declared.DeclaredAt.ToString("O", CultureInfo.InvariantCulture);
            if (declared.Version is { } version)
                operation.Parameters["ExchangeRateVersion"] = version;
        }

        if (!string.IsNullOrWhiteSpace(request.Purpose))
            operation.Parameters["Purpose"] = request.Purpose!.Trim();

        return operation;
    }

    private static ExchangeReceipt BuildReceipt(
        string receiptReference,
        Guid? operationId,
        ExchangeReceiptState state,
        ExchangeRequest request,
        ExchangeCallerContext caller,
        string platformAssetId,
        BigInteger platformAmount,
        BigInteger projectUnitAmount,
        TenantDeclaredRate? declaredRate,
        ExchangeGroupSubmission? submission,
        string? holdId,
        string? projectCreditAmount,
        string? valueMinorUnits,
        DateTime createdAt)
    {
        var buying = request.Direction == ExchangeDirection.Buy;
        var projectAssetId = request.ProjectAssetId.Trim();

        return new ExchangeReceipt
        {
            ReceiptReference = receiptReference,
            OperationId = operationId,
            State = state,
            IsTerminal = state != ExchangeReceiptState.AwaitingReconciliation,
            RequiresReconciliation = state == ExchangeReceiptState.AwaitingReconciliation,
            Replayed = false,
            Direction = request.Direction,
            UserId = caller.TargetUserId,
            ProjectReference = request.ProjectReference.Trim(),
            AmountSpent = (buying ? platformAmount : projectUnitAmount)
                .ToString(CultureInfo.InvariantCulture),
            AmountSpentAssetId = buying ? platformAssetId : projectAssetId,
            AmountReceived = (buying ? projectUnitAmount : platformAmount)
                .ToString(CultureInfo.InvariantCulture),
            AmountReceivedAssetId = buying ? projectAssetId : platformAssetId,
            ProjectCreditAmount = projectCreditAmount,
            SettlementReference = submission?.GroupReference,
            SettlementLegReferences = submission?.LegReferences ?? Array.Empty<string>(),
            HoldReference = holdId,
            HoldReleasesAt = request.ReleaseNotBefore,
            Rate = declaredRate?.MinorUnitsPerBaseUnit.ToString(CultureInfo.InvariantCulture),
            RateCurrencyCode = declaredRate?.CurrencyCode,
            RateSource = declaredRate?.Source,
            RateVersion = declaredRate?.Version,
            RateAsOf = declaredRate?.DeclaredAt,
            ValueMinorUnits = valueMinorUnits,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    // ── Idempotency and failure surfaces ──────────────────────────────────────

    private async Task<AZOAResult<ExchangeReceipt>> FailAndReturnAsync(
        string ledgerKey, string? message, string? code)
    {
        var text = string.IsNullOrWhiteSpace(message) ? UnexpectedFailureMessage : message!;
        await _idempotencyStore.FailAsync(ledgerKey, text, CancellationToken.None);
        return code is null
            ? AZOAResult<ExchangeReceipt>.Failure(text)
            : AZOAResult<ExchangeReceipt>.FailureWithCode(text, code);
    }

    /// <summary>
    /// Fails the claim and returns a refusal that has been through the consumer-safe mapper.
    /// Rail text is written for an operator and names accounts, assets and acceptance steps,
    /// none of which may reach a consumer or a sponsor.
    /// </summary>
    private async Task<AZOAResult<ExchangeReceipt>> FailAndReturnRailAsync(
        string ledgerKey, string? railMessage, string? code = null)
    {
        await _idempotencyStore.FailAsync(
            ledgerKey, railMessage ?? UnexpectedFailureMessage, CancellationToken.None);

        // A refusal already written in plain language keeps its meaning; one carrying rail
        // vocabulary is REPLACED wholesale by a compile-time constant, so an address, an asset
        // identifier or the words "opt-in" cannot escape even if a rail starts emitting them
        // somewhere new.
        if (code is not null && !ConsumerSafeErrors.LeaksRailDetail(railMessage))
            return AZOAResult<ExchangeReceipt>.FailureWithCode(railMessage!, code);

        var mapped = ConsumerSafeErrors.Map(railMessage);
        return AZOAResult<ExchangeReceipt>.FailureWithCode(mapped.Message, code ?? mapped.Code);
    }

    private static AZOAResult<T> RailRefusal<T>(string? railMessage)
    {
        var mapped = ConsumerSafeErrors.Map(railMessage);
        return AZOAResult<T>.FailureWithCode(mapped.Message, mapped.Code);
    }

    private static AZOAResult<ExchangeReceipt> ReplayFromRecord(IdempotencyRecord record)
        => IdempotencyReplay.ReplayFromRecord<ExchangeReceipt>(
            record,
            IdempotencyReplay.DeserializeForReplay<ExchangeReceipt>,
            receipt => receipt.Replayed = true,
            "Duplicate request: returning the result of the original exchange (not re-executed).",
            "Duplicate request: the original exchange result could not be replayed.",
            "The original exchange failed.",
            "An exchange is already being processed. Retry once the original request settles.");

    private static ExchangeReceiptState ResolveState(IdempotencyState ledgerState, string? operationStatus)
    {
        if (ledgerState == IdempotencyState.Failed
            || string.Equals(operationStatus, OperationStatus.Failed, StringComparison.Ordinal))
        {
            return ExchangeReceiptState.Failed;
        }

        return ledgerState == IdempotencyState.Completed && IsSuccessfulTerminal(operationStatus)
            ? ExchangeReceiptState.Completed
            : ExchangeReceiptState.AwaitingReconciliation;
    }

    private static bool IsSuccessfulTerminal(string? status) =>
        status is OperationStatus.Exchanged or OperationStatus.Completed or OperationStatus.Transferred;

    private static bool IsAbsentOperation(AZOAResult<IBlockchainOperation> result)
        => string.Equals(result.Code, AzoaErrorCodes.NotFound, StringComparison.Ordinal)
           || string.Equals(result.Message, "Operation not found.", StringComparison.Ordinal);

    private static AZOAResult<ExchangeReceipt> Invalid(string message)
        => AZOAResult<ExchangeReceipt>.FailureWithCode(message, AzoaErrorCodes.InvalidRequest);

    private static AZOAResult<ExchangeReceipt> NotAllowed(string message)
        => AZOAResult<ExchangeReceipt>.FailureWithCode(message, AzoaErrorCodes.OperationNotAllowed);

    private static AZOAResult<ExchangeReceipt> NotFound()
        => AZOAResult<ExchangeReceipt>.FailureWithCode(
            "Exchange receipt not found.", AzoaErrorCodes.NotFound);

    private static AZOAResult<ExchangeReceipt> Unavailable()
        => AZOAResult<ExchangeReceipt>.FailureWithCode(
            UnavailableMessage, AzoaErrorCodes.DependencyUnavailable);

    // ── Local shapes ──────────────────────────────────────────────────────────

    private sealed record ResolvedAccounts(IWallet Wallet, string ReserveAddress, string TreasuryAddress);

    /// <summary>Which node-side commitment an exchange took, so a failure can hand it back.</summary>
    private enum ExchangeCommitmentKind
    {
        /// <summary>Units were drawn from the partition and are on their way to the recipient.</summary>
        PartitionIssue = 0,

        /// <summary>Units were committed to a hold, which drew the partition itself.</summary>
        Hold = 1,

        /// <summary>Units came back to the partition on a sell.</summary>
        PartitionReturn = 2,
    }

    private sealed record ExchangeCommitment(
        ExchangeCommitmentKind Kind,
        string? HoldId,
        bool TreasurySpent,
        string? PlatformAssetId = null);

    /// <summary>
    /// The exchange's own idempotency identity. It shares the allocation LEDGER but never the
    /// allocation KEY SPACE: one client key must not address two different operations.
    /// </summary>
    internal static class ExchangeIdempotency
    {
        private const string ReceiptCorrelationDomain = "azoa:exchange-receipt:v1:";

        public static ExchangeIdempotencyValue CreateFromClientKey(Guid apiKeyId, string clientIdempotencyKey)
        {
            if (apiKeyId == Guid.Empty)
                throw new ArgumentException("API key id is required.", nameof(apiKeyId));
            if (string.IsNullOrWhiteSpace(clientIdempotencyKey))
                throw new ArgumentException("Idempotency key is required.", nameof(clientIdempotencyKey));

            var ledgerKey = $"exchange:{apiKeyId}:{clientIdempotencyKey.Trim()}";
            var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ReceiptCorrelationDomain + ledgerKey));
            return new ExchangeIdempotencyValue(
                ledgerKey, Convert.ToHexString(digest).ToLowerInvariant());
        }
    }

    /// <summary>Ledger identity for internal use plus the public-safe correlation.</summary>
    internal sealed record ExchangeIdempotencyValue(string LedgerKey, string Correlation);
}
