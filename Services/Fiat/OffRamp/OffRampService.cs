// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
using System.Text.Json;
using SurrealForge.Client;
using UserRecord = AZOA.WebAPI.Persistence.SurrealDb.Models.User;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Fiat.OnRamp;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>What a person may cash out right now, in money, plus why the rest may not leave yet.</summary>
/// <param name="Available">Whether this node offers cashing out at all.</param>
/// <param name="AccountReady">Whether their payout account can actually receive money.</param>
/// <param name="CashableMinorUnits">The most they could ask for today.</param>
/// <param name="OnHoldMinorUnits">Money-equivalent of what is still inside a reversal window.</param>
/// <param name="AvailableFrom">When the oldest held part becomes cashable, when that is knowable.</param>
/// <param name="MinimumAmountMinorUnits">The smallest cash-out this node will send.</param>
/// <param name="MaximumAmountMinorUnits">The largest cash-out this node will send in one go.</param>
/// <param name="CurrencyCode">The money it will be paid in.</param>
/// <param name="StatusMessage">One sentence a person can act on.</param>
public sealed record CashOutQuote(
    bool Available,
    bool AccountReady,
    long CashableMinorUnits,
    long OnHoldMinorUnits,
    DateTimeOffset? AvailableFrom,
    long MinimumAmountMinorUnits,
    long MaximumAmountMinorUnits,
    string CurrencyCode,
    string StatusMessage);

/// <summary>What a caller asks for. The amount is MONEY; the node works out the units.</summary>
/// <param name="SubjectUserId">Whose account is cashing out. Never a body field.</param>
/// <param name="TenantId">The tenant driving it on their behalf, when one is.</param>
/// <param name="RequestKey">Caller-stable key; the same key resumes one cash-out.</param>
/// <param name="AmountMinorUnits">The money to send, in minor units.</param>
public sealed record CashOutRequest(
    Guid SubjectUserId, Guid? TenantId, string RequestKey, long AmountMinorUnits);

/// <summary>One cash-out as a person sees it. No chain words, no provider ids.</summary>
public sealed record CashOutView(
    string Id,
    string Status,
    string StatusMessage,
    long AmountMinorUnits,
    string CurrencyCode,
    DateTimeOffset RequestedAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset? ReversedAt)
{
    /// <summary>Consumer sentences for each durable status. The row's words are never shown raw.</summary>
    public static string MessageFor(FiatPayout row) => row.Status switch
    {
        FiatPayoutStatuses.Requested => "Getting your money ready to send.",
        FiatPayoutStatuses.UnitsCollected => "On its way to your bank.",
        FiatPayoutStatuses.BankPending => "Your bank transfer is being processed.",
        FiatPayoutStatuses.Paid => "Sent to your bank.",
        FiatPayoutStatuses.Reversed => "Your bank sent this payment back. Support is looking into it.",
        FiatPayoutStatuses.Returned => "This cash-out did not go through and your balance was restored.",
        FiatPayoutStatuses.Refused => row.StatusReason ?? "This cash-out could not be started.",
        _ => "This cash-out needs a person to look at it. Support has been notified.",
    };

    /// <summary>Projects one row for the person it belongs to.</summary>
    public static CashOutView From(FiatPayout row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new CashOutView(
            row.Id,
            row.Status,
            MessageFor(row),
            row.AmountMinorUnits,
            row.CurrencyCode,
            row.RequestedAt,
            row.PaidAt,
            row.ReversedAt);
    }
}

/// <summary>What one step of the off-ramp saga concluded.</summary>
/// <param name="Row">The cash-out as it now stands.</param>
/// <param name="Definite">
/// True when the node reached an answer it will not change by trying again. False means the
/// outcome could not be established, which is the only case the saga may spend a retry on.
/// </param>
/// <param name="IndeterminateReason">Why the answer is not definite yet. Null when it is.</param>
public sealed record OffRampStepOutcome(FiatPayout Row, bool Definite, string? IndeterminateReason);

/// <summary>
/// Work → stake → dollars, closed. See <c>Services/Fiat/OffRamp/AGENTS.md</c>.
/// </summary>
public interface IOffRamp
{
    /// <summary>
    /// What this person could cash out today, and why the rest is held. Priced by the SAME scope
    /// a request from this caller would use, so the figure shown and the figure charged cannot
    /// come from two different registered prices.
    /// </summary>
    Task<AZOAResult<CashOutQuote>> GetQuoteAsync(
        Guid subjectUserId, Guid? tenantId = null, CancellationToken ct = default);

    /// <summary>Claims a cash-out and starts the saga. Idempotent on the request key.</summary>
    Task<AZOAResult<CashOutView>> RequestAsync(CashOutRequest request, CancellationToken ct = default);

    /// <summary>Step 1: the units go back to the reserve. Nothing is paid yet.</summary>
    Task<AZOAResult<OffRampStepOutcome>> CollectUnitsAsync(string payoutId, CancellationToken ct = default);

    /// <summary>Step 2: the money is sent to the person's own payout account.</summary>
    Task<AZOAResult<OffRampStepOutcome>> SendMoneyAsync(string payoutId, CancellationToken ct = default);

    /// <summary>Reconciles a bank event against the durable connected account and provider facts.</summary>
    Task<FiatEventOutcome> ReconcileBankPayoutAsync(
        string payoutId, string accountId, string providerPayoutId, CancellationToken ct = default);

    /// <summary>Reconciles failed bank funding and restores only proven returned value.</summary>
    Task<AZOAResult<FiatPayout>> RecoverFailedBankAsync(string payoutId, Guid actor, string reason, CancellationToken ct = default);

    /// <summary>Compensation: give the units back and close the cash-out.</summary>
    Task<AZOAResult<OffRampStepOutcome>> AbandonAsync(
        string payoutId, string reason, CancellationToken ct = default);

    /// <summary>Parks a cash-out at a status only a person can move it off.</summary>
    Task<AZOAResult<FiatPayout>> ParkForOperatorAsync(
        string payoutId, string reason, CancellationToken ct = default);

    /// <summary>One person's own cash-out history, newest first; a tenant sees only cash-outs it drove.</summary>
    Task<AZOAResult<IReadOnlyList<CashOutView>>> ListForUserAsync(
        Guid subjectUserId, int limit, Guid? tenantId = null, CancellationToken ct = default);
}

/// <summary>
/// The cash-out saga: units out of the person's account and back into the reserve, then money out
/// of the platform's balance and into their own bank.
///
/// <para>The order is not negotiable and it is the reason this is a saga rather than a method. If
/// the money went first, a node that crashed between the two halves would have paid for units it
/// never took. Taking the units first means the failure mode is the survivable one — the person
/// is briefly out of units with no money — and that state is <em>named</em>
/// (<see cref="FiatPayoutStatuses.UnitsCollected"/>), durable, and compensated by giving the units
/// straight back.</para>
/// </summary>
public sealed class OffRampService : IOffRamp
{
    /// <summary>Idempotency operation type stamped on every cash-out value move.</summary>
    public const string PayoutOperationType = "fiat-off-ramp";

    /// <summary>The reason recorded when a value move's outcome could not be established.</summary>
    public const string AmbiguousMoveReason =
        "A cash-out move was submitted and its outcome could not be established. Confirm it "
        + "against the rail before retrying: repeating it blind could take the units twice.";

    private const int MaxReasonLength = 512;

    private readonly FiatOptions _fiat;
    private readonly OffRampOptions _options;
    private readonly OnRampOptions _onRamp;
    private readonly IFiatPayoutLedger _payouts;
    private readonly IOnRampLotLedger _lots;
    private readonly ITenantPriceScheduleStore _schedules;
    private readonly IPayoutAccountService _accounts;
    private readonly IStripeConnectGateway _connect;
    private readonly IPlatformTokenGenesis _platformToken;
    private readonly IPlatformReserve _reserve;
    private readonly IBlockchainProviderFactory _providerFactory;
    private readonly IWalletStore _wallets;
    private readonly IIdempotencyStore _idempotency;
    private readonly IRealValueKycGate _kycGate;
    private readonly ISagaCoordinator _sagas;
    private readonly ILogger<OffRampService> _logger;
    private readonly TimeProvider _clock;
    private BankPayoutProcessor BankPayouts => new(_fiat, _payouts, _connect, _logger, _clock);

    public OffRampService(
        FiatOptions fiat,
        OffRampOptions options,
        OnRampOptions onRamp,
        IFiatPayoutLedger payouts,
        IOnRampLotLedger lots,
        ITenantPriceScheduleStore schedules,
        IPayoutAccountService accounts,
        IStripeConnectGateway connect,
        IPlatformTokenGenesis platformToken,
        IPlatformReserve reserve,
        IBlockchainProviderFactory providerFactory,
        IWalletStore wallets,
        IIdempotencyStore idempotency,
        IRealValueKycGate kycGate,
        ISagaCoordinator sagas,
        ILogger<OffRampService> logger,
        TimeProvider? clock = null)
    {
        _fiat = fiat ?? throw new ArgumentNullException(nameof(fiat));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _onRamp = onRamp ?? throw new ArgumentNullException(nameof(onRamp));
        _payouts = payouts ?? throw new ArgumentNullException(nameof(payouts));
        _lots = lots ?? throw new ArgumentNullException(nameof(lots));
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _platformToken = platformToken ?? throw new ArgumentNullException(nameof(platformToken));
        _reserve = reserve ?? throw new ArgumentNullException(nameof(reserve));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _wallets = wallets ?? throw new ArgumentNullException(nameof(wallets));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));
        _kycGate = kycGate ?? throw new ArgumentNullException(nameof(kycGate));
        _sagas = sagas ?? throw new ArgumentNullException(nameof(sagas));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    // ─── Read ───

    /// <inheritdoc />
    public async Task<AZOAResult<CashOutQuote>> GetQuoteAsync(
        Guid subjectUserId, Guid? tenantId = null, CancellationToken ct = default)
    {
        if (!IsAvailable())
            return AZOAResult<CashOutQuote>.Success(Unavailable(), "Success");

        var priced = await ResolveContextAsync(tenantId, ct);
        if (priced.IsError || priced.Result is not { } context)
            return AZOAResult<CashOutQuote>.Success(Unavailable(priced.Message), "Success");

        var account = await _accounts.GetStatusAsync(subjectUserId, ct);
        var accountReady = !account.IsError && account.Result?.ReadyToReceive == true;

        var availability = await ReadAvailabilityAsync(subjectUserId, context, excludePayoutId: null, ct);
        if (availability.IsError || availability.Result is not { } available)
            return AZOAResult<CashOutQuote>.FailureWithCode(
                availability.Message, FiatPayoutErrors.PayoutBusyCode);

        var cashable = OffRampAging.QuoteMinorUnits(available.CashableBaseUnits, context.UnitsPerMinorUnit);
        var onHold = OffRampAging.QuoteMinorUnits(available.LockedBaseUnits, context.UnitsPerMinorUnit);

        return AZOAResult<CashOutQuote>.Success(
            new CashOutQuote(
                Available: true,
                AccountReady: accountReady,
                CashableMinorUnits: cashable,
                OnHoldMinorUnits: onHold,
                AvailableFrom: available.NextUnlockAt,
                MinimumAmountMinorUnits: _options.MinimumAmountMinorUnits,
                MaximumAmountMinorUnits: _options.MaximumAmountMinorUnits,
                CurrencyCode: context.CurrencyCode,
                StatusMessage: accountReady
                    ? (cashable >= _options.MinimumAmountMinorUnits
                        ? "Ready when you are."
                        : "Not enough available to cash out yet.")
                    : PayoutAccountService.SetupNeededMessage),
            "Success");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<CashOutView>>> ListForUserAsync(
        Guid subjectUserId, int limit, Guid? tenantId = null, CancellationToken ct = default)
    {
        var rows = await _payouts.ListForUserAsync(subjectUserId, limit, ct);
        if (rows.IsError)
            return AZOAResult<IReadOnlyList<CashOutView>>.Failure(rows.Message);

        // F-H8: a tenant reads only the cash-outs it drove for this person, never another tenant's
        // or the person's own first-party ones.
        var visible = tenantId is null
            ? rows.Result!
            : rows.Result!.Where(row => DroveBy(row, tenantId)).ToList();
        return AZOAResult<IReadOnlyList<CashOutView>>.Success(
            visible.Select(CashOutView.From).ToList(), "Success");
    }

    // ─── Claim ───

    /// <inheritdoc />
    public async Task<AZOAResult<CashOutView>> RequestAsync(
        CashOutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsAvailable())
            return FiatPayoutErrors.RailUnavailable<CashOutView>();

        if (request.SubjectUserId == Guid.Empty)
            return AZOAResult<CashOutView>.FailureWithCode(
                "An authenticated account is required to cash out.", AzoaErrorCodes.InvalidRequest);

        if (string.IsNullOrWhiteSpace(request.RequestKey))
            return AZOAResult<CashOutView>.FailureWithCode(
                "A stable request key is required so a repeated request cannot pay twice.",
                AzoaErrorCodes.InvalidRequest);

        if (request.AmountMinorUnits < _options.MinimumAmountMinorUnits
            || request.AmountMinorUnits > _options.MaximumAmountMinorUnits)
        {
            return AZOAResult<CashOutView>.FailureWithCode(
                FormatAmountBounds(), AzoaErrorCodes.InvalidRequest);
        }

        // A cash-out is a value action, so D46 applies — and because the registered gate is
        // wrapped by RiskHoldValueGate, this ALSO refuses an account paused by an open reversal.
        // Somebody who charged back a card must not be able to walk the units out the other door.
        var approval = request.TenantId is { } gateTenant
            ? await _kycGate.RequireCurrentApprovalAsync(request.SubjectUserId, gateTenant, ct)
            : await _kycGate.RequireCurrentApprovalAsync(request.SubjectUserId, ct);
        if (approval.IsError)
            return AZOAResult<CashOutView>.FailureWithCode(approval.Message, AzoaErrorCodes.Forbidden);

        var accountId = await _accounts.RequireReadyAccountAsync(request.SubjectUserId, ct);
        if (accountId.IsError)
            return AZOAResult<CashOutView>.FailureWithCode(
                accountId.Message, accountId.Code ?? AzoaErrorCodes.InvalidRequest);

        var resolved = await ResolveContextAsync(request.TenantId, ct);
        if (resolved.IsError || resolved.Result is not { } context)
            return AZOAResult<CashOutView>.FailureWithCode(
                resolved.Message, FiatPayoutErrors.PayoutBusyCode);

        if (!OffRampAging.TryQuoteUnits(
                request.AmountMinorUnits, context.UnitsPerMinorUnit, out var quantity, out var quoteError))
        {
            return AZOAResult<CashOutView>.FailureWithCode(quoteError, AzoaErrorCodes.InvalidRequest);
        }

        var now = _clock.GetUtcNow();
        var candidate = new FiatPayout
        {
            Id = FiatPayout.RecordIdFor(request.SubjectUserId, request.RequestKey, _fiat.ActiveEnvironment.Name, _providerFactory.DefaultNetwork.ToString()),
            Environment = _fiat.ActiveEnvironment.Name,
            ChainNetwork = _providerFactory.DefaultNetwork.ToString(),
            SubjectUserId = SurrealLink.ToLink(UserRecord.SchemaNameConst, request.SubjectUserId.ToString("N"))!,
            RequestKey = request.RequestKey.Trim(),
            TenantId = request.TenantId is { } tenant ? SurrealLink.ToLink(UserRecord.SchemaNameConst, tenant.ToString("N")) : null,
            ChainType = context.ChainType,
            AssetId = context.AssetId,
            QuantityBaseUnits = OffRampAging.Format(quantity),
            AmountMinorUnits = request.AmountMinorUnits,
            CurrencyCode = context.CurrencyCode.ToUpperInvariant(),
            PriceScheduleId = SurrealLink.ToLink(TenantPriceSchedule.SchemaNameConst, context.PriceScheduleId),
            PriceScheduleVersion = context.PriceScheduleVersion,
            UnitsPerMinorUnit = PriceScheduleMath.FormatRate(context.UnitsPerMinorUnit),
            ConnectedAccountId = accountId.Result,
            Status = FiatPayoutStatuses.Requested,
            AttemptCount = 0,
            RequestedAt = now,
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var claim = await _payouts.ClaimAsync(candidate, ct);
        if (claim.IsError || claim.Result is null)
            return FiatPayoutErrors.RailBusy<CashOutView>();

        // A repeat of the same key resumes the cash-out that already exists. Returning it verbatim
        // — rather than re-planning it — is what makes a double-tapped button one payment. The
        // replay is honoured only for the same driver (F-H8): another tenant, or the person
        // themself, reusing the key gets a conflict rather than somebody else's cash-out.
        if (!claim.Result.Created)
            return DroveBy(claim.Result.Row, request.TenantId)
                ? AZOAResult<CashOutView>.Success(CashOutView.From(claim.Result.Row), "Success")
                : AZOAResult<CashOutView>.FailureWithCode(
                    "That request key is already in use.", AzoaErrorCodes.Conflict);

        var row = claim.Result.Row;

        // The availability check runs AFTER the claim so a refusal is durable and legible, and so
        // this row's own committed units are excluded from the figure it is checked against.
        var availability = await ReadAvailabilityAsync(
            request.SubjectUserId, context, excludePayoutId: row.Id, ct);
        if (availability.IsError || availability.Result is not { } available)
            return await RefuseAsync(row, "Your available balance could not be read. Nothing was sent.", ct);

        if (quantity > available.CashableBaseUnits)
        {
            var reason = available.LockedBaseUnits > BigInteger.Zero
                ? "Some of your balance is still on hold from a recent payment and cannot be cashed out yet."
                : "That is more than you have available to cash out.";
            return await RefuseAsync(row, reason, ct);
        }

        var consumed = await ConsumeLotsAsync(request.SubjectUserId, quantity, ct);
        if (consumed.IsError)
            return await RefuseAsync(row, "Your available balance could not be reserved. Nothing was sent.", ct);

        row.WriteConsumedLots(consumed.Result!.Receipt);
        row.AgedQuantityBaseUnits = OffRampAging.Format(consumed.Result.AgedBaseUnits);
        row.UpdatedAt = _clock.GetUtcNow();

        var saved = await SaveAsync(row, ct);
        if (saved.IsError)
        {
            // The lots were drawn down and the receipt did not land. Refusing here would strand
            // those units as consumed against a cash-out nobody will ever run, so the draws are
            // handed straight back before the refusal.
            await ReleaseLotsAsync(request.SubjectUserId, consumed.Result.Receipt, ct);
            return await RefuseAsync(row, "This cash-out could not be recorded. Nothing was sent.", ct);
        }

        var started = await StartSagaAsync(saved.Result!, ct);
        if (started is { } startFailure)
        {
            // Nothing has moved yet, so the honest recovery is to give the reservation back and
            // let the person try again — rather than leave a claimed row that no processor owns.
            await ReleaseLotsAsync(request.SubjectUserId, consumed.Result.Receipt, ct);
            return await RefuseAsync(row, startFailure, ct);
        }

        return AZOAResult<CashOutView>.Success(CashOutView.From(saved.Result!), "Success");
    }

    /// <summary>Hands the cash-out to the durable saga. Null on success; the refusal otherwise.</summary>
    private async Task<string?> StartSagaAsync(FiatPayout row, CancellationToken ct)
    {
        try
        {
            await _sagas.StartAsync(
                OffRampSaga.Name,
                OffRampSaga.CorrelationKeyFor(row.Id),
                new OffRampCollectPayload(row.Id, row.SubjectUserId),
                ct);
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception, "Cash-out {PayoutId} was claimed but its saga could not be started.", row.Id);
            return "This cash-out could not be started. Nothing left your account; try again shortly.";
        }
    }

    // ─── Saga step 1: the units come back ───

    /// <inheritdoc />
    public async Task<AZOAResult<OffRampStepOutcome>> CollectUnitsAsync(
        string payoutId, CancellationToken ct = default)
    {
        var read = await _payouts.GetAsync(payoutId, ct);
        if (read.IsError)
            return AZOAResult<OffRampStepOutcome>.Failure(read.Message);
        if (read.Result is not { } row)
            return AZOAResult<OffRampStepOutcome>.Failure($"Cash-out '{payoutId}' was not found.");
        if (row.Environment != _fiat.ActiveEnvironment.Name || row.ChainNetwork != _providerFactory.DefaultNetwork.ToString())
            return AZOAResult<OffRampStepOutcome>.Failure("This payout belongs to a different payment or chain environment.");

        // Already past this step, closed, or waiting on a person: nothing for the step to do.
        if (!string.Equals(row.Status, FiatPayoutStatuses.Requested, StringComparison.Ordinal))
            return Definite(row);

        if (!Guid.TryParseExact(SurrealLink.FromLink(row.SubjectUserId), "N", out var subjectUserId) || subjectUserId == Guid.Empty)
            return await ParkAsync(row, "This cash-out names no account to take units from.", ct);

        // Collect always takes the full claimed, availability-checked quantity; see AGENTS.md §collect-quantity.
        var quantity = OffRampAging.ParseBaseUnits(row.QuantityBaseUnits);
        if (quantity <= BigInteger.Zero)
            return await ParkAsync(row, "This cash-out names no units to take.", ct);

        var wallet = await FindWalletAsync(subjectUserId, row.ChainType, ct);
        if (wallet.Wallet is null)
            return await ParkAsync(row, wallet.Error, ct);

        var reserveAccount = await _reserve.GetAccountAsync(row.ChainType, ct);
        if (reserveAccount.IsError || reserveAccount.Result is null)
            return Indeterminate(row, $"The platform account could not be resolved: {reserveAccount.Message}");

        if (!TryGetProvider(row.ChainType, out var provider, out var providerError))
            return Indeterminate(row, providerError);

        var key = MoveIdempotencyKey(row.Id, "collect");
        var claim = await _idempotency.TryClaimAsync(key, PayoutOperationType, ct);
        if (!claim.Won)
        {
            // A replay. Completed means the units already moved and only the row is behind; a
            // previous attempt that failed BEFORE anything was broadcast is an ordinary retry;
            // and only a previous attempt that could not establish its outcome parks, because
            // re-sending over that would take the units twice.
            switch (ClassifyReplay(claim))
            {
                case ClaimReplay.Completed:
                    return await MarkUnitsCollectedAsync(row, claim.Record.ResultPayload, ct);
                case ClaimReplay.Ambiguous:
                    return await ParkAsync(row, AmbiguousMoveReason, ct);
                default:
                    return Indeterminate(row, "A collection for this cash-out is already in progress.");
            }
        }

        var signingContext = BuildUserSigningContext(row, subjectUserId, wallet.Wallet.Id);

        var moved = await provider.TransferAsync(
            row.AssetId,
            wallet.Wallet.Address,
            reserveAccount.Result.Address,
            RailAmount.OfChainAsset(quantity, row.AssetId),
            signingContext,
            ct);

        if (moved.IsError)
        {
            // The provider proves every precondition against the node BEFORE signing, so a
            // refusal here means nothing was broadcast.
            await _idempotency.FailAsync(key, moved.Message, ct);
            await CountAttemptAsync(row, moved.Message, ct);
            return Indeterminate(row, $"The units could not be collected: {moved.Message}");
        }

        if (IsPendingConfirmation(moved.Message))
        {
            await _idempotency.FailAsync(key, AmbiguousMoveReason, ct);
            return await ParkAsync(row, AmbiguousMoveReason, ct);
        }

        await _idempotency.CompleteAsync(key, moved.Result ?? string.Empty, ct);

        // The units are back in the reserve, so the partition they were issued from gets its
        // availability back. Without this draw the on-ramp partition would shrink by every
        // cash-out the node successfully completed.
        var draw = await _reserve.DrawAsync(
            new ReserveDrawRequest(
                row.ChainType, row.AssetId, _options.ReservePartition,
                ReserveDrawKind.Return, quantity, key,
                // The node's own off-ramp rail: neither the asset (a node-written payout row)
                // nor the partition (node configuration) can be named by a caller.
                ReserveDrawAuthority.NodeOperator),
            ct);

        if (draw.IsError)
        {
            // The value moved and the counter did not. The draw carries its own replay anchor
            // (the same key), so a later pass re-applies it without moving anything again.
            _logger.LogError(
                "Cash-out {PayoutId} returned {Amount} base units to the reserve but the partition "
                + "counter was not updated: {Detail}",
                row.Id, row.QuantityBaseUnits, draw.Message);
        }

        return await MarkUnitsCollectedAsync(row, moved.Result, ct);
    }

    // ─── Saga step 2: the money goes out ───

    /// <inheritdoc />
    public async Task<AZOAResult<OffRampStepOutcome>> SendMoneyAsync(
        string payoutId, CancellationToken ct = default)
    {
        var read = await _payouts.GetAsync(payoutId, ct);
        if (read.IsError)
            return AZOAResult<OffRampStepOutcome>.Failure(read.Message);
        if (read.Result is not { } row)
            return AZOAResult<OffRampStepOutcome>.Failure($"Cash-out '{payoutId}' was not found.");
        if (row.Environment != _fiat.ActiveEnvironment.Name || row.ChainNetwork != _providerFactory.DefaultNetwork.ToString())
            return AZOAResult<OffRampStepOutcome>.Failure("This payout belongs to a different payment or chain environment.");

        if (row.Status == FiatPayoutStatuses.BankPending)
            return await BankPayouts.SendAsync(row, ct);

        if (string.Equals(row.Status, FiatPayoutStatuses.Paid, StringComparison.Ordinal)
            || FiatPayoutStatuses.NeedsPerson(row.Status)
            || FiatPayoutStatuses.IsSettled(row.Status))
        {
            return Definite(row);
        }

        if (!string.Equals(row.Status, FiatPayoutStatuses.UnitsCollected, StringComparison.Ordinal))
            return Indeterminate(row, "The units for this cash-out have not been collected yet.");

        if (!Guid.TryParseExact(SurrealLink.FromLink(row.SubjectUserId), "N", out var subjectUserId) || subjectUserId == Guid.Empty)
            return await ParkAsync(row, "This cash-out names no account to pay.", ct);

        // The destination is re-resolved from the person's OWN durable onboarding record on every
        // attempt, never carried from the request. A payout account can lose its ability to
        // receive money between the request and the send, and paying a blocked account is how a
        // transfer becomes a reversal.
        var accountId = await _accounts.RequireReadyAccountAsync(subjectUserId, ct);
        if (accountId.IsError)
        {
            await CountAttemptAsync(row, accountId.Message, ct);
            return await ExhaustedAsync(row, accountId.Message, ct)
                ?? Indeterminate(row, accountId.Message);
        }

        if (!string.Equals(row.ConnectedAccountId, accountId.Result, StringComparison.Ordinal))
            return await ParkAsync(row, "The payout account changed. Reconcile before sending money.", ct);

        var manual = await _connect.ConfigureManualPayoutsAsync(
            _fiat.CredentialsFor(_fiat.ActiveEnvironment), accountId.Result!, ct);
        if (manual.IsError || manual.Result?.PayoutSchedule != "manual"
            || manual.Result.AccountId != row.ConnectedAccountId)
            return Indeterminate(row, "The payout account could not be configured for bank transfers.");

        var key = MoveIdempotencyKey(row.Id, "pay");
        var claim = await _idempotency.TryClaimAsync(key, PayoutOperationType, ct);
        if (!claim.Won)
        {
            switch (ClassifyReplay(claim))
            {
                case ClaimReplay.Completed:
                    return await MarkTransferFundedAsync(row, claim.Record.ResultPayload, ct);
                case ClaimReplay.Ambiguous:
                    return await ParkAsync(row, AmbiguousMoveReason, ct);
                default:
                    return Indeterminate(row, "A payment for this cash-out is already in progress.");
            }
        }

        var transfer = await _connect.CreatePayoutTransferAsync(
            _fiat.CredentialsFor(_fiat.ActiveEnvironment),
            new StripeTransferRequest
            {
                PayoutId = row.Id,
                SubjectUserId = SurrealLink.FromLink(row.SubjectUserId)!,
                ConnectedAccountId = accountId.Result!,
                AmountMinorUnits = row.AmountMinorUnits,
                Currency = row.CurrencyCode.ToLowerInvariant(),
                // Stable across retries: the provider returns the SAME transfer rather than
                // sending a second one, which is the only thing standing between a retry storm
                // and paying somebody twice.
                IdempotencyKey = "azoa-payout-" + row.Id,
            },
            ct);

        if (transfer.IsError || transfer.Result is null)
        {
            await CountAttemptAsync(row, transfer.Message, ct);

            // A refusal (not a busy rail) will not become a success by waiting. Give the units
            // back rather than leaving somebody with neither units nor money.
            if (string.Equals(transfer.Code, FiatPayoutErrors.PayoutRefusedCode, StringComparison.Ordinal))
            {
                await _idempotency.FailAsync(key, transfer.Message, ct);
                return await AbandonAsync(row.Id, transfer.Message, ct);
            }

            return await ParkAsync(row,
                "The funding transfer outcome is uncertain. Reconcile it before returning units.", ct);
        }

        if (transfer.Result.Livemode != (_fiat.ActiveEnvironment.Kind == RailEnvironmentKind.Live)
            || transfer.Result.AmountMinorUnits != row.AmountMinorUnits
            || !string.Equals(transfer.Result.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase)
            || transfer.Result.Destination != row.ConnectedAccountId)
        {
            // An inconsistent transfer requires reconciliation before any further money move.
            await _idempotency.CompleteAsync(key, transfer.Result.TransferId, ct);
            return await ParkAsync(
                row,
                "The funding transfer does not match this cash-out. Reconcile it before doing anything else.",
                ct);
        }

        await _idempotency.CompleteAsync(key, transfer.Result.TransferId, ct);
        return await MarkTransferFundedAsync(row, transfer.Result.TransferId, ct);
    }

    // ─── Compensation ───

    /// <inheritdoc />
    public async Task<AZOAResult<OffRampStepOutcome>> AbandonAsync(
        string payoutId, string reason, CancellationToken ct = default)
    {
        var read = await _payouts.GetAsync(payoutId, ct);
        if (read.IsError)
            return AZOAResult<OffRampStepOutcome>.Failure(read.Message);
        if (read.Result is not { } row)
            return AZOAResult<OffRampStepOutcome>.Failure($"Cash-out '{payoutId}' was not found.");
        if (row.Environment != _fiat.ActiveEnvironment.Name || row.ChainNetwork != _providerFactory.DefaultNetwork.ToString())
            return AZOAResult<OffRampStepOutcome>.Failure("This payout belongs to a different payment or chain environment.");

        if (FiatPayoutStatuses.IsSettled(row.Status))
            return Definite(row);

        var paymentClaim = await _idempotency.GetAsync(MoveIdempotencyKey(row.Id, "pay"), ct);
        if (row.ProviderTransferId is not null || row.BankPayoutStartedAt is not null
            || paymentClaim?.State is IdempotencyState.Completed or IdempotencyState.InProgress)
            return await ParkAsync(row, "Money may have left the platform. Reconcile it before returning units.", ct);

        if (FiatPayoutStatuses.NeedsPerson(row.Status))
        {
            var collection = await _idempotency.GetAsync(MoveIdempotencyKey(row.Id, "collect"), ct);
            if (collection?.State != IdempotencyState.Completed) return Definite(row);
            row.UnitsCollectedTransactionId ??= collection.ResultPayload;
        }

        if (string.Equals(row.Status, FiatPayoutStatuses.Requested, StringComparison.Ordinal))
        {
            // "Requested" normally means nothing has moved. It can also mean the collection
            // landed and its row write did not, so the claim is consulted rather than the status
            // trusted: closing this as "nothing moved" would leave a person short of the units
            // the reserve is already holding.
            var collected = await _idempotency.GetAsync(MoveIdempotencyKey(row.Id, "collect"), ct);
            if (collected?.State != IdempotencyState.Completed)
            {
                await ReleaseLotsAsync(SubjectOf(row), row.ReadConsumedLots(), ct);
                return await SettleAsync(row, FiatPayoutStatuses.Refused, reason, ct);
            }

            row.UnitsCollectedTransactionId ??= collected.ResultPayload;
            row.Status = FiatPayoutStatuses.UnitsCollected;
        }

        // The units were taken and the money never left. Handing them straight back is the only
        // honest compensation; recording a debt is what happens when even that fails.
        var returned = await ReturnUnitsAsync(row, ct);
        if (returned is { } failure)
        {
            row.UnitsOwedBaseUnits = row.QuantityBaseUnits;
            return await ParkAsync(
                row,
                $"{reason} The units could not be given back either ({failure}).",
                ct);
        }

        await ReleaseLotsAsync(SubjectOf(row), row.ReadConsumedLots(), ct);
        return await SettleAsync(row, FiatPayoutStatuses.Returned, reason, ct);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatPayout>> ParkForOperatorAsync(
        string payoutId, string reason, CancellationToken ct = default)
    {
        var read = await _payouts.GetAsync(payoutId, ct);
        if (read.IsError)
            return AZOAResult<FiatPayout>.Failure(read.Message);
        if (read.Result is not { } row)
            return AZOAResult<FiatPayout>.Failure($"Cash-out '{payoutId}' was not found.");

        var parked = await ParkAsync(row, reason, ct);
        return parked.IsError
            ? AZOAResult<FiatPayout>.Failure(parked.Message)
            : AZOAResult<FiatPayout>.Success(parked.Result!.Row, "Parked.");
    }

    // ─── Source-aged consumption ───

    /// <summary>The FIFO draw plan applied to the lots, with the receipt to record on the row.</summary>
    private sealed record ConsumptionReceipt(
        IReadOnlyDictionary<string, string> Receipt, BigInteger AgedBaseUnits);

    /// <summary>
    /// Draws <paramref name="quantity"/> from matured on-ramp lots, oldest first, and writes the
    /// watermark back to each. Whatever is left over came from units with no lot at all.
    /// </summary>
    private async Task<AZOAResult<ConsumptionReceipt>> ConsumeLotsAsync(
        Guid subjectUserId, BigInteger quantity, CancellationToken ct)
    {
        var lots = await _lots.ListIssuedForUserAsync(subjectUserId, ct);
        if (lots.IsError)
            return AZOAResult<ConsumptionReceipt>.Failure(lots.Message);

        var plan = OffRampAging.PlanConsumption(lots.Result!, quantity, _clock.GetUtcNow());
        var receipt = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var draw in plan.Draws)
        {
            var lot = lots.Result!.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, draw.LotId, StringComparison.Ordinal));
            if (lot is null)
                continue;

            var consumed = OffRampAging.ParseBaseUnits(lot.ConsumedBaseUnits) + draw.BaseUnits;
            lot.ConsumedBaseUnits = OffRampAging.Format(consumed);
            lot.UpdatedAt = _clock.GetUtcNow();

            var expected = lot.Version;
            lot.Version = expected + 1;
            var saved = await _lots.UpdateIfVersionAsync(lot, expected, ct);
            if (saved.IsError)
            {
                // Somebody else moved this lot underneath us — a chargeback recovery, or a
                // concurrent cash-out. Hand back whatever this pass had already drawn rather than
                // leaving half a reservation behind, and let the caller refuse.
                lot.Version = expected;
                await ReleaseLotsAsync(subjectUserId, receipt, ct);
                return AZOAResult<ConsumptionReceipt>.Failure(
                    $"On-ramp lot '{lot.Id}' changed while this cash-out was being reserved.");
            }

            receipt[draw.LotId] = OffRampAging.Format(draw.BaseUnits);
        }

        return AZOAResult<ConsumptionReceipt>.Success(
            new ConsumptionReceipt(receipt, plan.AgedBaseUnits), "Success");
    }

    /// <summary>Gives consumed lot units back when a cash-out never happened.</summary>
    /// <remarks>
    /// The lots are re-read through the person's own issued list rather than by id, because a lot
    /// that has since been REVERSED by a chargeback is no longer issued — and handing its
    /// watermark back would re-age units a refund already took away. Skipping it is the correct
    /// answer, not a missed case.
    /// </remarks>
    private async Task ReleaseLotsAsync(
        Guid subjectUserId, IReadOnlyDictionary<string, string> receipt, CancellationToken ct)
    {
        if (receipt.Count == 0 || subjectUserId == Guid.Empty)
            return;

        var lots = await _lots.ListIssuedForUserAsync(subjectUserId, ct);
        if (lots.IsError)
        {
            _logger.LogError(
                "On-ramp lots for account {UserId} could not be re-read to release a cash-out reservation: {Detail}",
                subjectUserId, lots.Message);
            return;
        }

        foreach (var (lotId, amount) in receipt)
        {
            var drawn = OffRampAging.ParseBaseUnits(amount);
            if (drawn <= BigInteger.Zero)
                continue;

            var lot = lots.Result!.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, lotId, StringComparison.Ordinal));
            if (lot is null)
            {
                _logger.LogWarning(
                    "On-ramp lot {LotId} is no longer issued, so {Amount} base units reserved against "
                    + "it were not released.",
                    lotId, amount);
                continue;
            }

            var consumed = OffRampAging.ParseBaseUnits(lot.ConsumedBaseUnits) - drawn;
            lot.ConsumedBaseUnits = OffRampAging.Format(consumed > BigInteger.Zero ? consumed : BigInteger.Zero);
            lot.UpdatedAt = _clock.GetUtcNow();

            var expected = lot.Version;
            lot.Version = expected + 1;
            var saved = await _lots.UpdateIfVersionAsync(lot, expected, ct);
            if (saved.IsError)
            {
                lot.Version = expected;
                _logger.LogError(
                    "On-ramp lot {LotId} could not release {Amount} base units: {Detail}",
                    lotId, amount, saved.Message);
            }
        }
    }

    // ─── Helpers ───

    /// <summary>The asset, the price and the currency one cash-out is settled against.</summary>
    private sealed record OffRampContext(
        string ChainType,
        string AssetId,
        string CurrencyCode,
        decimal UnitsPerMinorUnit,
        string? PriceScheduleId,
        long PriceScheduleVersion);

    /// <summary>
    /// Resolves the platform unit and the registered price that converts it back into money —
    /// the SAME schedule the on-ramp sells at, so a person cannot be sold units at one price and
    /// bought out at another the node never published.
    /// </summary>
    private async Task<AZOAResult<OffRampContext?>> ResolveContextAsync(
        Guid? tenantId, CancellationToken ct)
    {
        var tokenState = await _platformToken.GetStateAsync(ct);
        if (tokenState.IsError || tokenState.Result is null)
            return AZOAResult<OffRampContext?>.Failure(
                "Cashing out is not available on this node right now.");

        var definition = tokenState.Result.Definition;

        var scope = PriceScheduleScope.For(tenantId);
        var scoped = await _schedules.GetLatestAsync(scope, definition.ChainType, definition.AssetId, ct);
        if (scoped.IsError)
            return AZOAResult<OffRampContext?>.Failure(scoped.Message);

        var schedule = scoped.Result;
        if (schedule is null && !PriceScheduleScope.IsNode(scope) && _onRamp.AllowNodeDefaultScheduleFallback)
        {
            var fallback = await _schedules.GetLatestAsync(
                PriceScheduleScope.Node, definition.ChainType, definition.AssetId, ct);
            if (fallback.IsError)
                return AZOAResult<OffRampContext?>.Failure(fallback.Message);
            schedule = fallback.Result;
        }

        // A retired latest version means the scope has stopped trading, in BOTH directions.
        // Reaching back to an older price to buy somebody out would pay at a price nobody is
        // offering any more — the same refusal the on-ramp makes, read backwards.
        if (schedule is not { IsActive: true })
            return AZOAResult<OffRampContext?>.Failure(
                "Cashing out is paused on this node while the current price is being updated.");

        var now = _clock.GetUtcNow();
        if (schedule.DeclaredAt > now + PriceScheduleLimits.FutureDeclarationTolerance
            || now - schedule.DeclaredAt > PriceScheduleLimits.MaxDeclarationAge)
        {
            return AZOAResult<OffRampContext?>.Failure(
                "Cashing out is paused on this node while the current price is being updated.");
        }

        return AZOAResult<OffRampContext?>.Success(
            new OffRampContext(
                definition.ChainType,
                definition.AssetId,
                schedule.CurrencyCode,
                schedule.UnitsPerMinorUnit,
                schedule.RecordId,
                schedule.Version),
            "Success");
    }

    /// <summary>
    /// What this person may cash out: what they hold, less what a still-reversible payment bought
    /// them, less what an unfinished cash-out has already promised.
    /// </summary>
    private async Task<AZOAResult<CashOutAvailability?>> ReadAvailabilityAsync(
        Guid subjectUserId, OffRampContext context, string? excludePayoutId, CancellationToken ct)
    {
        var wallet = await FindWalletAsync(subjectUserId, context.ChainType, ct);
        if (wallet.Wallet is null)
        {
            // No account on this chain means no units to cash out. Zero is the honest answer, not
            // an error: a person who has never received anything is not a fault.
            return AZOAResult<CashOutAvailability?>.Success(
                new CashOutAvailability(BigInteger.Zero, BigInteger.Zero, BigInteger.Zero, null), "Success");
        }

        if (!TryGetAsaModule(context.ChainType, out var asa, out var moduleError))
            return AZOAResult<CashOutAvailability?>.Failure(moduleError);

        var holdingRead = await asa!.GetAssetHoldingAsync(context.AssetId, wallet.Wallet.Address, ct);
        if (holdingRead.IsError)
            return AZOAResult<CashOutAvailability?>.Failure(
                $"The account's balance could not be read: {holdingRead.Message}");

        var lots = await _lots.ListIssuedForUserAsync(subjectUserId, ct);
        if (lots.IsError)
            return AZOAResult<CashOutAvailability?>.Failure(lots.Message);

        var committing = await _payouts.ListCommittingForUserAsync(subjectUserId, ct);
        if (committing.IsError)
            return AZOAResult<CashOutAvailability?>.Failure(committing.Message);

        if (lots.Result!.Any(lot => lot.Environment == "unbound" || lot.ChainNetwork == "unbound")
            || committing.Result!.Any(payout => payout.Environment == "unbound" || payout.ChainNetwork == "unbound"))
            return AZOAResult<CashOutAvailability?>.Failure("Historical payment records need authoritative environment classification before cash-out.");
        var matchingLots = lots.Result!.Where(lot => lot.Environment == _fiat.ActiveEnvironment.Name
            && lot.ChainNetwork == _providerFactory.DefaultNetwork.ToString()).ToArray();
        var now = _clock.GetUtcNow();
        var committed = BigInteger.Zero;
        foreach (var open in committing.Result!.Where(payout => payout.Environment == _fiat.ActiveEnvironment.Name
            && payout.ChainNetwork == _providerFactory.DefaultNetwork.ToString()))
        {
            if (excludePayoutId is not null && string.Equals(open.Id, excludePayoutId, StringComparison.Ordinal))
                continue;
            committed += OffRampAging.ParseBaseUnits(open.QuantityBaseUnits);
        }

        return AZOAResult<CashOutAvailability?>.Success(
            new CashOutAvailability(
                OffRampAging.ParseBaseUnits(holdingRead.Result),
                OffRampAging.LockedBaseUnits(matchingLots, now),
                committed,
                OffRampAging.NextUnlockAt(matchingLots, now)),
            "Success");
    }

    /// <summary>Moves the units back out of the reserve. Null on success; the refusal otherwise.</summary>
    private async Task<string?> ReturnUnitsAsync(FiatPayout row, CancellationToken ct, BigInteger? requestedQuantity = null, string? reservedKey = null)
    {
        if (!Guid.TryParseExact(SurrealLink.FromLink(row.SubjectUserId), "N", out var subjectUserId) || subjectUserId == Guid.Empty)
            return "the account to return them to could not be resolved";

        var quantity = requestedQuantity ?? OffRampAging.ParseBaseUnits(row.QuantityBaseUnits);
        if (quantity <= BigInteger.Zero)
            return null;

        var wallet = await FindWalletAsync(subjectUserId, row.ChainType, ct);
        if (wallet.Wallet is null)
            return wallet.Error;

        var reserveAccount = await _reserve.GetAccountAsync(row.ChainType, ct);
        if (reserveAccount.IsError || reserveAccount.Result is null)
            return reserveAccount.Message;

        if (!TryGetProvider(row.ChainType, out var provider, out var providerError))
            return providerError;

        var key = reservedKey ?? MoveIdempotencyKey(row.Id, "return");
        if (_idempotency is not IValueMoveReceiptStore receipts
            || !provider.TryGetModule<IValueTransferTransactionModule>(out var module) || module is null)
            return "This chain requires durable prepared transfer support before units can be returned.";
        var claim = await _idempotency.TryClaimAsync(key, "fiat-off-ramp-prepared", ct);
        if (!claim.Won && claim.Record.State == IdempotencyState.Completed)
        {
            row.UnitsReturnedTransactionId = claim.Record.ResultPayload;
            var repaired = await _reserve.DrawAsync(new ReserveDrawRequest(row.ChainType, row.AssetId,
                _options.ReservePartition, ReserveDrawKind.Issue, quantity, key, ReserveDrawAuthority.NodeOperator), ct);
            return repaired.IsError ? repaired.Message : null;
        }
        if (claim.Record.State != IdempotencyState.InProgress || claim.Record.OperationType != "fiat-off-ramp-prepared")
            return "The prior value move requires authoritative reconciliation before another transfer.";
        var encoded = await receipts.GetPreparedAsync(key, ct);
        if (encoded is null)
        {
            var generation = await receipts.GetPreparationGenerationAsync(key, ct);
            var prepared = await module.PrepareValueTransferAsync(new(row.ChainType, _providerFactory.DefaultNetwork,
                row.AssetId, reserveAccount.Result.Address, wallet.Wallet.Address, OffRampAging.Format(quantity),
                SigningContext.PlatformFor(row.ChainType), $"value:{key}:{generation}"), ct);
            if (prepared.IsError || prepared.Result is null) return prepared.Message;
            await receipts.TryPrepareAsync(key, JsonSerializer.Serialize(prepared.Result), ct, generation);
            encoded = await receipts.GetPreparedAsync(key, ct);
        }
        var durable = encoded is null ? null : JsonSerializer.Deserialize<PreparedValueTransfer>(encoded);
        if (durable is null || durable.Intent.ChainType != row.ChainType || durable.Intent.Network != _providerFactory.DefaultNetwork
            || durable.Intent.AssetId != row.AssetId || durable.Intent.Sender != reserveAccount.Result.Address
            || durable.Intent.Recipient != wallet.Wallet.Address || durable.Intent.QuantityBaseUnits != OffRampAging.Format(quantity))
            return "The prepared transaction does not match the reserved restitution.";
        var observed = await module.ObserveValueTransferAsync(durable, ct);
        if (!observed.IsError && observed.Result == ChainConfirmation.FailedOnChain)
        {
            await receipts.TryClearPreparedAsync(key, encoded!, ct);
            return "The original prepared transfer is authoritatively unable to take effect; the next recovery can prepare a replacement.";
        }
        if (!observed.IsError && observed.Result == ChainConfirmation.Unknown)
        {
            await module.SubmitValueTransferAsync(durable, ct);
            observed = await module.ObserveValueTransferAsync(durable, ct);
        }
        if (observed.IsError || observed.Result != ChainConfirmation.Confirmed)
        {
            row.ReturnPendingTransactionId = durable.TransactionId;
            row.ReturnPendingIdempotencyKey = key;
            var pendingSaved = await SaveAsync(row, ct);
            return pendingSaved.IsError ? pendingSaved.Message : AmbiguousMoveReason;
        }
        await _idempotency.CompleteAsync(key, durable.TransactionId, ct);

        var draw = await _reserve.DrawAsync(
            new ReserveDrawRequest(
                row.ChainType, row.AssetId, _options.ReservePartition,
                ReserveDrawKind.Issue, quantity, key,
                ReserveDrawAuthority.NodeOperator),
            ct);

        if (draw.IsError)
        {
            _logger.LogError(
                "Cash-out {PayoutId} gave {Amount} base units back but the partition counter was "
                + "not updated: {Detail}",
                row.Id, row.QuantityBaseUnits, draw.Message);
            return draw.Message;
        }

        row.UnitsReturnedTransactionId = durable.TransactionId;
        return null;
    }

    private async Task<AZOAResult<OffRampStepOutcome>> MarkUnitsCollectedAsync(
        FiatPayout row, string? transactionId, CancellationToken ct)
    {
        row.Status = FiatPayoutStatuses.UnitsCollected;
        row.StatusReason = null;
        row.UnitsCollectedTransactionId = string.IsNullOrWhiteSpace(transactionId) ? null : transactionId;
        row.UnitsCollectedAt = _clock.GetUtcNow();
        row.UpdatedAt = row.UnitsCollectedAt.Value;

        var saved = await SaveAsync(row, ct);
        return saved.IsError
            // The units moved and the row did not. Retrying converges — the idempotency claim
            // replays instead of re-sending — and saying "done" here would strand a person whose
            // units are gone behind a row that says nothing happened.
            ? Indeterminate(row, saved.Message)
            : Definite(saved.Result!);
    }

    /// <inheritdoc />
    public async Task<FiatEventOutcome> ReconcileBankPayoutAsync(
        string payoutId, string accountId, string providerPayoutId, CancellationToken ct = default)
    {
        var read = await _payouts.GetAsync(payoutId, ct);
        if (read.IsError)
            return FiatEventOutcome.Failed;
        if (read.Result is not { } row)
            return FiatEventOutcome.Ignored;
        return await BankPayouts.ReconcileAsync(row, accountId, providerPayoutId, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<FiatPayout>> RecoverFailedBankAsync(string payoutId, Guid actor, string reason, CancellationToken ct = default)
    {
        var read = await _payouts.GetAsync(payoutId, ct);
        if (read.IsError || read.Result is not { } row) return AZOAResult<FiatPayout>.Failure(read.Message);
        if (!_fiat.Enabled || row.Environment != _fiat.ActiveEnvironment.Name || row.ChainNetwork != _providerFactory.DefaultNetwork.ToString())
            return AZOAResult<FiatPayout>.Failure("Recovery requires the original enabled payment environment and chain network.");
        return await new FailedBankPayoutRecovery(_fiat, _payouts, _connect, _clock, RestoreReservedUnitsAsync)
            .RecoverAsync(row, actor, reason, ct);
    }

    private async Task<string?> RestoreReservedUnitsAsync(FiatPayout row, BigInteger quantity, string key, CancellationToken ct)
    {
        var current = await _payouts.GetAsync(row.Id, ct);
        if (current.IsError || current.Result is not { } fresh || fresh.Version != row.Version
            || fresh.RestitutionKey != key || fresh.Status != FiatPayoutStatuses.Reversed)
            return "The restitution reservation changed; reconcile before moving units.";
        return await ReturnUnitsAsync(row, ct, quantity, key);
    }

    private async Task<AZOAResult<OffRampStepOutcome>> MarkTransferFundedAsync(
        FiatPayout row, string? transferId, CancellationToken ct)
    {
        row.Status = FiatPayoutStatuses.BankPending;
        row.StatusReason = null;
        row.ProviderTransferId = string.IsNullOrWhiteSpace(transferId) ? row.ProviderTransferId : transferId;
        row.UpdatedAt = _clock.GetUtcNow();

        var saved = await SaveAsync(row, ct);
        return saved.IsError ? Indeterminate(row, saved.Message) : await BankPayouts.SendAsync(saved.Result!, ct);
    }

    private async Task<AZOAResult<OffRampStepOutcome>> SettleAsync(
        FiatPayout row, string status, string reason, CancellationToken ct)
    {
        row.Status = status;
        row.StatusReason = Truncate(reason, MaxReasonLength);
        row.UpdatedAt = _clock.GetUtcNow();

        var saved = await SaveAsync(row, ct);
        return saved.IsError ? Indeterminate(row, saved.Message) : Definite(saved.Result!);
    }

    private async Task<AZOAResult<OffRampStepOutcome>> ParkAsync(
        FiatPayout row, string reason, CancellationToken ct)
    {
        row.Status = FiatPayoutStatuses.NeedsOperator;
        row.StatusReason = Truncate(reason, MaxReasonLength);
        row.UpdatedAt = _clock.GetUtcNow();

        var saved = await SaveAsync(row, ct);
        if (saved.IsError)
            return Indeterminate(row, saved.Message);

        _logger.LogError(
            "Cash-out {PayoutId} needs a person: {Reason}. {Amount} {Currency} minor units were "
            + "requested against {Quantity} base units.",
            row.Id, row.StatusReason, row.AmountMinorUnits, row.CurrencyCode, row.QuantityBaseUnits);

        return Definite(saved.Result!);
    }

    /// <summary>Records the refusal on the row so a person can see what happened and why.</summary>
    private async Task<AZOAResult<CashOutView>> RefuseAsync(
        FiatPayout row, string reason, CancellationToken ct)
    {
        await SettleAsync(row, FiatPayoutStatuses.Refused, reason, ct);
        return AZOAResult<CashOutView>.FailureWithCode(reason, AzoaErrorCodes.InvalidRequest);
    }

    /// <summary>Counts one attempt against the bound without moving the row off its status.</summary>
    private async Task CountAttemptAsync(FiatPayout row, string? reason, CancellationToken ct)
    {
        row.AttemptCount += 1;
        row.StatusReason = Truncate(reason ?? "The cash-out did not complete.", MaxReasonLength);
        row.UpdatedAt = _clock.GetUtcNow();
        await SaveAsync(row, ct);
    }

    /// <summary>Abandons the cash-out once the attempt budget is spent; null while it is not.</summary>
    private async Task<AZOAResult<OffRampStepOutcome>?> ExhaustedAsync(
        FiatPayout row, string reason, CancellationToken ct)
    {
        if (row.AttemptCount < _options.EffectiveMaxPayoutAttempts)
            return null;

        return await AbandonAsync(
            row.Id,
            $"The cash-out could not be sent within {_options.EffectiveMaxPayoutAttempts} attempts ({reason}).",
            ct);
    }

    /// <summary>Compare-and-swap save that advances the row's own version anchor.</summary>
    private async Task<AZOAResult<FiatPayout>> SaveAsync(FiatPayout row, CancellationToken ct)
    {
        var expected = row.Version;
        row.Version = expected + 1;

        var saved = await _payouts.UpdateIfVersionAsync(row, expected, ct);
        if (saved.IsError)
        {
            // Put the in-memory row back where the caller found it so a caller that continues does
            // not reason over a version this node never persisted.
            row.Version = expected;
        }

        return saved;
    }

    /// <summary>
    /// The person's own signing context, tenant-stamped when a tenant drove the cash-out so the
    /// custody seam runs the live consent check before any key decrypt.
    /// </summary>
    private static SigningContext BuildUserSigningContext(FiatPayout row, Guid subjectUserId, Guid walletId)
    {
        var context = SigningContext.ForUser(subjectUserId, walletId).ForChain(row.ChainType);

        return Guid.TryParseExact(row.TenantId is null ? null : SurrealLink.FromLink(row.TenantId), "N", out var tenantId) && tenantId != Guid.Empty
            ? context.ActingAs(tenantId, AzoaScopes.TransferSign, subjectUserId)
            : context;
    }

    private async Task<(IWallet? Wallet, string Error)> FindWalletAsync(
        Guid subjectUserId, string chainType, CancellationToken ct)
    {
        var wallets = await _wallets.GetByUserAsync(subjectUserId, ct);
        if (wallets.IsError)
            return (null, $"The account's wallet could not be read: {wallets.Message}");

        var match = wallets.Result?.FirstOrDefault(wallet =>
            string.Equals(wallet.ChainType, chainType, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(wallet.Address));

        return match is null
            ? (null, "This account has no balance to cash out yet.")
            : (match, string.Empty);
    }

    private bool TryGetProvider(string chainType, out IBlockchainProvider provider, out string error)
    {
        provider = null!;
        error = string.Empty;
        try
        {
            provider = _providerFactory.GetProvider(chainType, _providerFactory.DefaultNetwork);
            return true;
        }
        catch (BlockchainProviderNotFoundException)
        {
            error = $"No provider is available for '{chainType}', so this cash-out cannot settle.";
            return false;
        }
        catch (ValueRailProviderNotFoundException)
        {
            error = $"No provider is available for '{chainType}', so this cash-out cannot settle.";
            return false;
        }
    }

    private bool TryGetAsaModule(string chainType, out IAlgorandASAModule? module, out string error)
    {
        module = null;
        error = string.Empty;
        try
        {
            var provider = _providerFactory.GetProvider(chainType, _providerFactory.DefaultNetwork);
            if (!provider.TryGetModule<IAlgorandASAModule>(out module) || module is null)
            {
                error = $"The '{chainType}' rail cannot report an account balance, so nothing can be cashed out there.";
                return false;
            }

            return true;
        }
        catch (BlockchainProviderNotFoundException)
        {
            error = $"No provider is available for '{chainType}'.";
            return false;
        }
    }

    /// <summary>True only when BOTH the rail and the cash-out path are configured and switched on.</summary>
    private bool IsAvailable() =>
        _options.Enabled
        && _options.MissingKeys().Count == 0
        && _fiat.MissingKeysFor(_fiat.ActiveEnvironment).Count == 0;

    private CashOutQuote Unavailable(string? message = null) => new(
        Available: false,
        AccountReady: false,
        CashableMinorUnits: 0,
        OnHoldMinorUnits: 0,
        AvailableFrom: null,
        MinimumAmountMinorUnits: _options.MinimumAmountMinorUnits,
        MaximumAmountMinorUnits: _options.MaximumAmountMinorUnits,
        CurrencyCode: (_fiat.Checkout.Currency ?? "usd").ToUpperInvariant(),
        StatusMessage: message ?? PayoutAccountService.UnavailableMessage);

    private string FormatAmountBounds() => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        "Enter an amount between {0:0.00} and {1:0.00}.",
        _options.MinimumAmountMinorUnits / 100m,
        _options.MaximumAmountMinorUnits / 100m);

    /// <summary>The idempotency key one value move on one cash-out claims.</summary>
    public static string MoveIdempotencyKey(string payoutId, string leg)
        => $"{PayoutOperationType}:{leg}:{payoutId}";

    /// <summary>What a LOST idempotency claim means for the leg that lost it.</summary>
    private enum ClaimReplay
    {
        /// <summary>The move landed. Only this node's own bookkeeping is behind.</summary>
        Completed = 0,

        /// <summary>Nothing was broadcast, or another worker holds the claim. Try again later.</summary>
        Retryable = 1,

        /// <summary>A previous attempt could not establish its outcome. Never re-send over this.</summary>
        Ambiguous = 2,
    }

    /// <summary>
    /// Reads a lost claim. A previous attempt that failed BEFORE broadcasting is an ordinary
    /// retry; only one that recorded the ambiguity marker parks, because re-sending over an
    /// unknown outcome is how a cash-out takes somebody's units twice.
    /// </summary>
    private static ClaimReplay ClassifyReplay(IdempotencyClaim claim) => claim.Record.State switch
    {
        IdempotencyState.Completed => ClaimReplay.Completed,
        IdempotencyState.Failed =>
            (claim.Record.Error ?? string.Empty).Contains(AmbiguousMoveReason, StringComparison.Ordinal)
                ? ClaimReplay.Ambiguous
                : ClaimReplay.Retryable,
        _ => ClaimReplay.Retryable,
    };

    /// <summary>The account a row belongs to, or empty when the row names one this node cannot read.</summary>
    /// <summary>True when <paramref name="tenantId"/> (null = first-party) is the party that drove the cash-out.</summary>
    private static bool DroveBy(FiatPayout row, Guid? tenantId)
        => SurrealRecordGuid.ParseOptional(row.TenantId) == tenantId;

    private static Guid SubjectOf(FiatPayout row)
        => Guid.TryParseExact(SurrealLink.FromLink(row.SubjectUserId), "N", out var subjectUserId) ? subjectUserId : Guid.Empty;

    private static bool IsPendingConfirmation(string? message)
        => message?.Contains(OperationStatus.PendingConfirmationMarker, StringComparison.Ordinal) == true;

    private static AZOAResult<OffRampStepOutcome> Definite(FiatPayout row)
        => AZOAResult<OffRampStepOutcome>.Success(new OffRampStepOutcome(row, true, null), "Success");

    private static AZOAResult<OffRampStepOutcome> Indeterminate(FiatPayout row, string? reason)
        => AZOAResult<OffRampStepOutcome>.Success(
            new OffRampStepOutcome(row, false, reason), "Indeterminate.");

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
