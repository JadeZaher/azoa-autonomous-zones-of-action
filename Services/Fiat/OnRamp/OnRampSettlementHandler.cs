// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client;
using UserRecord = AZOA.WebAPI.Persistence.SurrealDb.Models.User;

namespace AZOA.WebAPI.Services.Fiat.OnRamp;

/// <summary>
/// The identity a rail-driven issuance is initiated under. Not a credential and not an API key:
/// there is no principal on a webhook, and inventing one would be worse than naming this.
/// </summary>
/// <remarks>
/// It exists because the allocation seam partitions its idempotency ledger by API-key id and
/// stamps an initiator on the durable operation. A fixed constant makes the on-ramp's ledger keys
/// stable across restarts — a per-process value would make every restart a second issuance for the
/// same payment. It grants nothing: no route accepts it, and no authorisation check consults it.
/// </remarks>
public static class OnRampSystemIdentity
{
    /// <summary>Stable id stamped as the initiator of a rail-driven issuance.</summary>
    public static readonly Guid SettlementAuthorityId =
        new("f1a70a11-0000-4000-8000-000000000001");
}

/// <summary>
/// Turns a CLEARED CARD PAYMENT into units in the payer's own custodial account, and writes the
/// lot row that records which units that money bought.
///
/// <para>It implements <see cref="IFiatSettlementHandler"/>, which is the marker
/// <c>FiatRailStartup.Validate</c> requires before the LIVE environment may boot at all.</para>
///
/// <para>See <c>Services/Fiat/OnRamp/AGENTS.md</c> for the invariants: the quantity is derived
/// from the provider's own confirmed total and the registered price schedule and from nothing
/// else; the recipient is read from the durable funding intent and never from webhook metadata;
/// and a payment this node will not credit becomes a visible, refundable lot rather than an
/// endless provider retry.</para>
/// </summary>
public sealed class OnRampSettlementHandler : IFiatSettlementHandler
{
    /// <summary>The routing word stamped on the durable operation. Read by the operation log (D16).</summary>
    public const string IssuancePurpose = "on-ramp";

    /// <summary>Event types that mean money for a funding intent has actually cleared.</summary>
    /// <remarks>Read-only: a shared mutable array would let one caller silently widen or narrow
    /// what this handler claims for every other caller.</remarks>
    public static readonly IReadOnlyList<string> HandledEventTypes = new[]
    {
        "checkout.session.completed",
        "checkout.session.async_payment_succeeded",
    };

    private readonly FiatOptions _fiat;
    private readonly OnRampOptions _options;
    private readonly IStripeGateway _gateway;
    private readonly IHostedSessionStore _sessions;
    private readonly ITenantPriceScheduleStore _schedules;
    private readonly IOnRampLotLedger _lots;
    private readonly IPlatformTokenGenesis _platformToken;
    private readonly IAllocationManager _allocations;
    private readonly IRealValueKycGate _kycGate;
    private readonly IValueRailProviderFactory _rails;
    private readonly ILogger<OnRampSettlementHandler> _logger;
    private readonly TimeProvider _clock;
    private readonly IBlockchainOperationStore? _operations;

    public OnRampSettlementHandler(
        FiatOptions fiat,
        OnRampOptions options,
        IStripeGateway gateway,
        IHostedSessionStore sessions,
        ITenantPriceScheduleStore schedules,
        IOnRampLotLedger lots,
        IPlatformTokenGenesis platformToken,
        IAllocationManager allocations,
        IRealValueKycGate kycGate,
        IValueRailProviderFactory rails,
        ILogger<OnRampSettlementHandler> logger,
        TimeProvider? clock = null,
        IBlockchainOperationStore? operations = null)
    {
        _fiat = fiat ?? throw new ArgumentNullException(nameof(fiat));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _lots = lots ?? throw new ArgumentNullException(nameof(lots));
        _platformToken = platformToken ?? throw new ArgumentNullException(nameof(platformToken));
        _allocations = allocations ?? throw new ArgumentNullException(nameof(allocations));
        _kycGate = kycGate ?? throw new ArgumentNullException(nameof(kycGate));
        _rails = rails ?? throw new ArgumentNullException(nameof(rails));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
        _operations = operations;
    }

    /// <inheritdoc />
    public string Name => "fiat-on-ramp";

    /// <inheritdoc />
    public bool Handles(string eventType) => HandledEventTypes.Contains(eventType, StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<FiatEventOutcome> ApplyAsync(FiatEventContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.IntentId is not { } intentId)
        {
            // A payment this node did not start. There is no account to credit.
            _logger.LogWarning(
                "A {EventType} delivery carried no funding-intent correlation; nothing was credited.",
                context.EventType);
            return FiatEventOutcome.Ignored;
        }

        if (string.IsNullOrWhiteSpace(context.CheckoutSessionId))
            return FiatEventOutcome.Ignored;

        var intentKey = intentId.ToString("N");

        // ── A lot that has already settled decides the answer, before anything is re-read ──
        var priorRead = await _lots.GetByIntentAsync(intentKey, ct);
        if (priorRead.IsError)
            return FiatEventOutcome.Failed;

        if (priorRead.Result is { } prior)
        {
            if (prior.Environment != context.Environment.Name || prior.Environment != _fiat.ActiveEnvironment.Name)
                return FiatEventOutcome.Failed;
            if (string.Equals(prior.Status, OnRampLotStatuses.Issued, StringComparison.Ordinal)
                || string.Equals(prior.Status, OnRampLotStatuses.Reversed, StringComparison.Ordinal)
                || prior.Status == OnRampLotStatuses.Refunded)
            {
                // Credited once. A redelivery must never credit a second time.
                return FiatEventOutcome.Applied;
            }

            if (OnRampLotStatuses.NeedsOperator(prior.Status) || prior.Status == OnRampLotStatuses.RefundPending)
            {
                // The provider is told to stop retrying, and the charge stays visible as a lot
                // that says exactly why nothing was credited. Retrying instead would burn the
                // provider's budget against a decision that will not change on its own.
                _logger.LogError(
                    "Funding intent {IntentId} is parked as {Status} ({Reason}); the payer was charged and "
                    + "has not been credited.",
                    intentKey, prior.Status, prior.StatusReason ?? "no reason recorded");
                return FiatEventOutcome.AcceptedPending;
            }
        }

        // ── The body is a TRIGGER. What happened is re-read from the provider ──
        var fetched = await _gateway.GetCheckoutSessionAsync(
            _fiat.CredentialsFor(context.Environment), context.CheckoutSessionId!, ct);
        if (fetched.IsError || fetched.Result is null)
        {
            _logger.LogWarning(
                "The payment session for funding intent {IntentId} could not be re-read; the delivery is "
                + "retryable and nothing was credited.",
                intentKey);
            return FiatEventOutcome.Failed;
        }

        var session = fetched.Result;

        // Defence in depth behind the controller's own check: a session belonging to the other
        // mode must never settle value here.
        if (session.Livemode != (context.Environment.Kind == RailEnvironmentKind.Live))
        {
            _logger.LogError(
                "The payment session for funding intent {IntentId} reports livemode={Livemode} on a rail "
                + "bound to {Environment}; nothing was credited.",
                intentKey, session.Livemode, context.Environment);
            return FiatEventOutcome.Failed;
        }

        // The correlation in the (signed) body has to agree with the correlation the PROVIDER
        // holds for the session. Disagreement means the two are not describing one payment.
        if (!string.IsNullOrWhiteSpace(session.ClientReferenceId)
            && !string.Equals(session.ClientReferenceId!.Trim(), intentKey, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "The payment session names funding intent {SessionIntent} while the delivery names "
                + "{DeliveryIntent}; nothing was credited.",
                session.ClientReferenceId, intentKey);
            return FiatEventOutcome.Failed;
        }

        switch (FiatRailProvider.ClassifySession(session))
        {
            case ChainConfirmation.Confirmed:
                break;

            // An asynchronous method that has not cleared. There is no money to convert yet, and
            // the provider's own async_payment_succeeded is what brings this back.
            case ChainConfirmation.Pending:
                return FiatEventOutcome.AcceptedPending;

            // Expired or failed: there is nothing to credit and nothing to retry.
            case ChainConfirmation.FailedOnChain:
                return FiatEventOutcome.Ignored;

            default:
                _logger.LogWarning(
                    "The payment session for funding intent {IntentId} could not be classified; the "
                    + "delivery is retryable and nothing was credited.",
                    intentKey);
                return FiatEventOutcome.Failed;
        }

        // ── The recipient comes from the node's OWN durable intent ──
        var intentRead = await _sessions.GetByIdAsync(intentId, ct);
        if (intentRead.IsError)
            return FiatEventOutcome.Failed;

        var intent = intentRead.Result;
        if (intent is null
            || !string.Equals(intent.FlowKind, HostedSessionFlowKinds.FiatCheckout, StringComparison.Ordinal)
            || !string.Equals(intent.ProviderKey, FiatRailIds.StripeProviderKey, StringComparison.Ordinal))
        {
            return FiatEventOutcome.Ignored;
        }

        if (!Guid.TryParseExact(intent.SubjectUserId, "N", out var subjectUserId) || subjectUserId == Guid.Empty)
        {
            _logger.LogError(
                "Funding intent {IntentId} names no account to credit; the payer was charged and has not "
                + "been credited.",
                intentKey);
            return FiatEventOutcome.Failed;
        }

        // Provider metadata is attacker-adjacent even under a valid signature (it is echoed from
        // the create call). Where it names a payer it must agree with the intent; the INTENT wins.
        if (context.SubjectUserId is { } metadataUserId && metadataUserId != subjectUserId)
        {
            _logger.LogError(
                "A payment for funding intent {IntentId} carried metadata naming a different account than "
                + "the intent does; nothing was credited.",
                intentKey);
            return FiatEventOutcome.Failed;
        }

        Guid? tenantId = Guid.TryParseExact(intent.TenantId, "N", out var parsedTenant) && parsedTenant != Guid.Empty
            ? parsedTenant
            : null;

        // ── The money, as the provider confirmed it ──
        var amountMinorUnits = session.AmountTotalMinorUnits ?? 0L;
        var currency = string.IsNullOrWhiteSpace(session.Currency)
            ? _fiat.Checkout.Currency
            : session.Currency!.Trim();

        // ── The asset those units are units OF ──
        var tokenState = await _platformToken.GetStateAsync(ct);
        if (tokenState.IsError || tokenState.Result is null)
        {
            _logger.LogError(
                "The platform unit could not be resolved ({Detail}); funding intent {IntentId} is charged "
                + "and uncredited until it can be.",
                tokenState.Message, intentKey);
            return FiatEventOutcome.Failed;
        }

        var definition = tokenState.Result.Definition;
        if (priorRead.Result is { } previous && previous.ChainNetwork != definition.Network)
            return FiatEventOutcome.Failed;

        // ── Claim the lot BEFORE anything else can fail ──
        // From here on every outcome is durable and visible: the row is the node's own record
        // that money was taken, and its attempt count is what bounds the provider's retries.
        var claim = await ClaimLotAsync(
            intentKey, subjectUserId, tenantId, definition, session, context, amountMinorUnits, currency, ct);
        if (claim.IsError || claim.Result is null)
            return FiatEventOutcome.Failed;

        var lot = claim.Result;

        if (lot.AttemptCount > _options.EffectiveMaxIssuanceAttempts)
        {
            return await ParkAsync(
                lot,
                OnRampLotStatuses.Stuck,
                $"No issuance succeeded within {_options.EffectiveMaxIssuanceAttempts.ToString(CultureInfo.InvariantCulture)} "
                + "attempts. Reconcile the operation before refunding or crediting.",
                ct);
        }

        if (!string.Equals(currency, _fiat.Checkout.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return await ParkAsync(
                lot,
                OnRampLotStatuses.Refused,
                $"The payment settled in '{currency}' while this node prices in "
                + $"'{_fiat.Checkout.Currency}'. The money is owed back.",
                ct);
        }

        // ── The price: registered, versioned, and never a figure the client chose ──
        var priced = await ResolveScheduleAsync(tenantId, definition.ChainType, definition.AssetId, ct);
        if (priced.IsError)
            return FiatEventOutcome.Failed;

        if (priced.Result is not { } schedule)
        {
            _logger.LogError(
                "No active price is registered for {ChainType}/{AssetId} in scope '{Scope}'; funding intent "
                + "{IntentId} is charged and uncredited until one is.",
                definition.ChainType, definition.AssetId, PriceScheduleScope.For(tenantId), intentKey);
            return FiatEventOutcome.Failed;
        }

        var now = _clock.GetUtcNow();
        if (schedule.DeclaredAt > now + PriceScheduleLimits.FutureDeclarationTolerance)
        {
            return await ParkAsync(
                lot,
                OnRampLotStatuses.Refused,
                "The registered price is dated after the payment it would price; the money is owed back.",
                ct);
        }

        if (now - schedule.DeclaredAt > PriceScheduleLimits.MaxDeclarationAge)
        {
            return await ParkAsync(
                lot,
                OnRampLotStatuses.Refused,
                "The registered price is older than this node will apply; re-register it and refund or "
                + "re-take the payment.",
                ct);
        }

        // ── The one conversion: provider-confirmed money x registered price ──
        if (!PriceScheduleMath.TryConvert(
                amountMinorUnits, schedule.UnitsPerMinorUnit, out var grossBaseUnits, out var conversionError))
        {
            return await ParkAsync(lot, OnRampLotStatuses.Refused, conversionError, ct);
        }

        lot.PriceScheduleId = SurrealLink.ToLink(TenantPriceSchedule.SchemaNameConst, schedule.RecordId);
        lot.PriceScheduleVersion = schedule.Version;
        lot.UnitsPerMinorUnit = PriceScheduleMath.FormatRate(schedule.UnitsPerMinorUnit);
        lot.GrossQuantityBaseUnits = PriceScheduleMath.FormatBaseUnits(grossBaseUnits);

        // ── The window these units are aged against, resolved BEFORE anything moves ──
        if (!TryResolveReversalWindow(context.Environment, out var reversalWindow))
        {
            _logger.LogError(
                "The '{RailId}' rail is not resolvable in {Environment}, so freshly issued units could not "
                + "be aged; funding intent {IntentId} is charged and uncredited.",
                FiatRailIds.UsdRail, context.Environment, intentKey);
            return FiatEventOutcome.Failed;
        }

        // ── D46: buying units is a value action and needs a current identity approval ──
        var approval = tenantId is { } gateTenant
            ? await _kycGate.RequireCurrentApprovalAsync(subjectUserId, gateTenant, ct)
            : await _kycGate.RequireCurrentApprovalAsync(subjectUserId, ct);
        if (approval.IsError)
        {
            return await ParkAsync(
                lot,
                OnRampLotStatuses.Refused,
                "The account's identity verification is not current, so the units cannot be issued. "
                + "The money is owed back.",
                ct);
        }

        // ── Issue from the reserve into the payer's own custodial account ──
        var request = new AllocationRequest
        {
            Kind = AllocationKind.IssueUnits,
            ChainType = definition.ChainType,
            Amount = PriceScheduleMath.FormatBaseUnits(grossBaseUnits),
            Name = definition.UnitName,
            AssetId = definition.AssetId,
            Partition = _options.ReservePartition,
            Purpose = IssuancePurpose,
        };

        var allocation = await _allocations.AllocateAsync(
            subjectUserId,
            request,
            callerUserId: subjectUserId,
            clientIdempotencyKey: IssuanceIdempotencyKey(intentKey),
            apiKeyId: OnRampSystemIdentity.SettlementAuthorityId.ToString(),
            actingTenantId: null,
            // The node's OWN on-ramp rail: the asset comes off the platform token
            // definition and the partition off node configuration, so neither can be named
            // by a caller. This is the POSITIVE operator signal the reserve now requires —
            // the missing tenant above grants nothing on its own (OI-21).
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        if (allocation.IsError || allocation.Result is null)
        {
            if (allocation.Code == ConsumerSafeErrorCodes.SettlementPending)
            {
                // The units were sent and the settlement reference has not been recorded yet. The
                // allocation ledger key holds the claim, so the provider's own retry replays the
                // ORIGINAL issuance rather than raising a second one — that convergence is why
                // this is retryable rather than a 2xx that ends the conversation.
                _logger.LogInformation(
                    "The issuance for funding intent {IntentId} is still settling; the delivery is "
                    + "retryable and no second issuance was raised.",
                    intentKey);
                await RecordAttemptOutcomeAsync(lot, "The issuance is still settling.", ct);
                return FiatEventOutcome.Failed;
            }

            _logger.LogError(
                "The issuance for funding intent {IntentId} did not complete: {Detail}",
                intentKey, allocation.Message);
            await RecordAttemptOutcomeAsync(lot, allocation.Message, ct);
            return FiatEventOutcome.Failed;
        }

        var result = allocation.Result;
        var issuedAt = _clock.GetUtcNow();

        lot.QuantityBaseUnits = string.IsNullOrWhiteSpace(result.NetAmount) ? lot.GrossQuantityBaseUnits : result.NetAmount;
        lot.NodeFeeBaseUnits = string.IsNullOrWhiteSpace(result.NodeFeeAmount) ? "0" : result.NodeFeeAmount;
        // operation_log record ids are the compact Guid form (SurrealBlockchainOperationStore
        // writes SurrealId.ToSurrealId); a dashed id would reference a row that does not exist.
        lot.OperationId = result.OperationId == Guid.Empty
            ? null : SurrealLink.ToLink(OperationLog.SchemaNameConst, result.OperationId.ToString("N"));
        var operation = _operations is null || result.OperationId == Guid.Empty
            ? null : await _operations.GetByIdAsync(result.OperationId, ct);
        if (operation is null || operation.IsError || operation.Result is not { } confirmed
            || confirmed.Status is not (OperationStatus.Completed or OperationStatus.Transferred)
            || string.IsNullOrWhiteSpace(confirmed.Parameters.GetValueOrDefault("TxHash")))
        {
            lot.StatusReason = "The issuance awaits authoritative chain confirmation.";
            lot.UpdatedAt = issuedAt;
            await SaveAsync(lot, ct);
            return FiatEventOutcome.AcceptedPending;
        }
        lot.Status = OnRampLotStatuses.Issued;
        lot.StatusReason = null;
        lot.IssuedAt = issuedAt;
        lot.ReversalWindowEnds = reversalWindow.TryGetReleaseTimeUtc(issuedAt, out var windowEnds) ? windowEnds : null;
        lot.UpdatedAt = issuedAt;

        var settled = await SaveAsync(lot, ct);
        if (settled.IsError)
        {
            // The units moved; the lot did not record it. A retry converges (the allocation
            // ledger key replays the same result), and saying "applied" here would leave units
            // in an account with no provenance for the cash-out rule to age.
            _logger.LogError(
                "The issuance for funding intent {IntentId} completed but its lot could not be recorded: "
                + "{Detail}",
                intentKey, settled.Message);
            return FiatEventOutcome.Failed;
        }

        _logger.LogInformation(
            "Funding intent {IntentId} credited {Quantity} base units of {AssetId} against "
            + "{Amount} {Currency} minor units at price version {Version}.",
            intentKey, lot.QuantityBaseUnits, definition.AssetId, amountMinorUnits, currency, schedule.Version);

        return FiatEventOutcome.Applied;
    }

    /// <summary>
    /// The deterministic idempotency key one funding intent's issuance claims. Stable across
    /// redeliveries and restarts, which is what makes a repeat a replay instead of a second
    /// issuance.
    /// </summary>
    public static string IssuanceIdempotencyKey(string intentKey) => $"{IssuancePurpose}:{intentKey}";

    /// <summary>
    /// The price that applies to a payment: the driving tenant's own registered schedule, or the
    /// operator's node-wide default when the payment has no tenant (or when the operator has
    /// explicitly allowed the default to stand in).
    /// </summary>
    private async Task<AZOAResult<RegisteredPriceSchedule?>> ResolveScheduleAsync(
        Guid? tenantId, string chainType, string assetId, CancellationToken ct)
    {
        var scope = PriceScheduleScope.For(tenantId);
        var scoped = await _schedules.GetLatestAsync(scope, chainType, assetId, ct);
        if (scoped.IsError)
            return AZOAResult<RegisteredPriceSchedule?>.Failure(scoped.Message);

        if (scoped.Result is { } schedule)
        {
            // A retired latest version means "we have stopped selling units", not "use an older
            // price": reaching back would sell at a price nobody is offering any more.
            return AZOAResult<RegisteredPriceSchedule?>.Success(schedule.IsActive ? schedule : null, "Success");
        }

        // Substituting the operator's price for a tenant's is an economic decision, so it happens
        // only when the operator has said it may.
        if (PriceScheduleScope.IsNode(scope) || !_options.AllowNodeDefaultScheduleFallback)
            return AZOAResult<RegisteredPriceSchedule?>.Success(null, "Not found.");

        var fallback = await _schedules.GetLatestAsync(PriceScheduleScope.Node, chainType, assetId, ct);
        if (fallback.IsError)
            return AZOAResult<RegisteredPriceSchedule?>.Failure(fallback.Message);

        return AZOAResult<RegisteredPriceSchedule?>.Success(
            fallback.Result is { IsActive: true } ? fallback.Result : null, "Success");
    }

    /// <summary>The rail's own chargeback window; refuses rather than assuming a constant.</summary>
    private bool TryResolveReversalWindow(RailEnvironment environment, out SettlementProfile profile)
    {
        profile = default;
        if (!_rails.TryGetRailProvider(FiatRailIds.UsdRail, environment, out var rail))
            return false;

        profile = rail.SettlementProfile;
        return true;
    }

    /// <summary>
    /// Creates the lot for this payment, or refreshes the one already there and counts this
    /// attempt against the retry bound.
    /// </summary>
    private async Task<AZOAResult<OnRampLot>> ClaimLotAsync(
        string intentKey,
        Guid subjectUserId,
        Guid? tenantId,
        PlatformTokenDefinition definition,
        StripeCheckoutSession session,
        FiatEventContext context,
        long amountMinorUnits,
        string currency,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var candidate = new OnRampLot
        {
            Id = OnRampLot.RecordIdFor(intentKey),
            IntentId = SurrealLink.ToLink(HostedSession.SchemaNameConst, intentKey)!,
            SubjectUserId = SurrealLink.ToLink(UserRecord.SchemaNameConst, subjectUserId.ToString("N"))!,
            TenantId = tenantId is { } tenant ? SurrealLink.ToLink(UserRecord.SchemaNameConst, tenant.ToString("N")) : null,
            ChainType = definition.ChainType,
            AssetId = definition.AssetId,
            FiatMinorUnits = amountMinorUnits,
            CurrencyCode = currency.ToUpperInvariant(),
            SourceCheckoutSessionId = session.SessionId,
            SourcePaymentIntentId = session.PaymentIntentId ?? context.PaymentIntentId,
            SourceChargeId = context.ChargeId,
            Status = OnRampLotStatuses.Pending,
            Environment = context.Environment.Name,
            ChainNetwork = definition.Network,
            AttemptCount = 1,
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var claimed = await _lots.ClaimAsync(candidate, ct);
        if (claimed.IsError || claimed.Result is null)
            return AZOAResult<OnRampLot>.Failure(claimed.Message);

        if (claimed.Result.Created)
            return AZOAResult<OnRampLot>.Success(claimed.Result.Row, "Claimed.");

        // A redelivery of a lot still Pending. Count the attempt and carry forward any provider
        // correlation the first delivery did not carry (a charge id arrives with later events).
        var existing = claimed.Result.Row;
        if (existing.Status != OnRampLotStatuses.Pending)
            return AZOAResult<OnRampLot>.Failure("The lot is no longer open for issuance.");
        existing.AttemptCount += 1;
        existing.SourceChargeId ??= context.ChargeId;
        existing.SourcePaymentIntentId ??= session.PaymentIntentId ?? context.PaymentIntentId;
        existing.UpdatedAt = now;

        var refreshed = await SaveAsync(existing, ct);
        return refreshed.IsError
            ? AZOAResult<OnRampLot>.Failure(refreshed.Message)
            : AZOAResult<OnRampLot>.Success(refreshed.Result!, "Claimed.");
    }

    /// <summary>
    /// Parks the lot at a status only a person can move it off, and tells the provider to stop
    /// retrying. The charge stays visible, with its reason, as money the node owes back.
    /// </summary>
    private async Task<FiatEventOutcome> ParkAsync(
        OnRampLot lot, string status, string reason, CancellationToken ct)
    {
        lot.Status = status;
        lot.StatusReason = Truncate(reason, 512);
        lot.UpdatedAt = _clock.GetUtcNow();

        var saved = await SaveAsync(lot, ct);
        if (saved.IsError)
        {
            // The park could not be recorded, so the provider must keep the delivery alive: a 2xx
            // here would end the conversation with no durable trace of the refusal.
            _logger.LogError(
                "Funding intent {IntentId} could not be parked as {Status}: {Detail}",
                lot.IntentId, status, saved.Message);
            return FiatEventOutcome.Failed;
        }

        _logger.LogError(
            "Funding intent {IntentId} is parked as {Status}: {Reason}. The payer was charged "
            + "{Amount} {Currency} minor units and has not been credited.",
            lot.IntentId, status, lot.StatusReason, lot.FiatMinorUnits, lot.CurrencyCode);

        return FiatEventOutcome.AcceptedPending;
    }

    /// <summary>Records why one attempt did not credit, without moving the lot off Pending.</summary>
    private async Task RecordAttemptOutcomeAsync(OnRampLot lot, string? reason, CancellationToken ct)
    {
        lot.StatusReason = Truncate(reason ?? "The issuance did not complete.", 512);
        lot.UpdatedAt = _clock.GetUtcNow();
        await SaveAsync(lot, ct);
    }

    /// <summary>Compare-and-swap save that advances the row's own version anchor.</summary>
    private async Task<AZOAResult<OnRampLot>> SaveAsync(OnRampLot lot, CancellationToken ct)
    {
        var expected = lot.Version;
        lot.Version = expected + 1;

        var saved = await _lots.UpdateIfVersionAsync(lot, expected, ct);
        if (saved.IsError)
        {
            // Put the in-memory row back where the caller found it so a caller that continues
            // does not reason over a version this node never persisted.
            lot.Version = expected;
        }

        return saved;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
