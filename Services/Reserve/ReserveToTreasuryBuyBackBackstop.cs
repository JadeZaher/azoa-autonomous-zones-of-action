// SPDX-License-Identifier: UNLICENSED
//
// OI-44 / OI-60. The buy-back backstop, as a movement the code makes rather than a seam it
// declares. Every phase moves the counter the promise is made against, so a promise this class
// did not manage to book simply does not exist.
//
// See Services/Reserve/AGENTS.md §buy-back.

using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>
/// The platform reserve standing behind a project's short buy-back: a set-aside on the
/// operator's declared partition, and then a real reserve to treasury movement on the rail the
/// sell itself settles on.
/// </summary>
/// <remarks>
/// The ownership question is answered by <see cref="ReserveDrawAuthority.NodeOperator"/>, and
/// that is the right authority here for a positive reason rather than a convenient one: neither
/// half of a backstop draw can be named by a caller. The unit is whatever
/// <c>PlatformTokenGenesis</c> recorded, and the partition is whatever
/// <see cref="BuyBackOptions.BackstopPartition"/> says, so there is nothing a request can point
/// at and nothing to steal. See <c>Services/Reserve/AGENTS.md</c>.
/// </remarks>
public sealed class ReserveToTreasuryBuyBackBackstop : IBuyBackBackstop
{
    /// <summary>Idempotency operation type every backstop movement is claimed under.</summary>
    public const string OperationType = "BuyBackBackstop";

    /// <summary>Shown when the backstop could not act. Never carries rail detail out of the node.</summary>
    public const string UnavailableMessage =
        "The platform could not stand behind this request right now. Nothing was moved.";

    /// <summary>
    /// Recorded on the move claim when the rail took the movement but never told us whether it
    /// landed. It is the difference between "did not happen" and "we do not know", and the two
    /// are settled differently: a movement that did not happen leaves a set-aside that may be
    /// handed back, while one we cannot establish leaves a set-aside nobody may touch.
    /// </summary>
    /// <remarks>
    /// Same shape and the same reason as
    /// <c>Services/Fiat/OffRamp/OffRampService.AmbiguousMoveReason</c>: a broadcast whose outcome
    /// is unknown is never re-sent and never assumed. <see cref="ReleaseReservationAsync"/> reads
    /// this back, so the marker is a control and not a log line.
    /// </remarks>
    public const string AmbiguousMoveReason =
        "A backstop movement was submitted and its outcome could not be established. Confirm it "
        + "against the rail: until then the set-aside must not be counted as issued and must not "
        + "be handed back.";

    private const string SetAsidePhase = "set-aside";
    private const string FillPhase = "fill";
    private const string MovePhase = "move";
    private const string ReleasePhase = "release";

    private readonly IPlatformReserve _reserve;
    private readonly IProjectTreasury _treasury;
    private readonly IBlockchainProviderFactory _providers;
    private readonly IIdempotencyStore _idempotency;
    private readonly ILogger<ReserveToTreasuryBuyBackBackstop>? _logger;

    public ReserveToTreasuryBuyBackBackstop(
        IPlatformReserve reserve,
        IProjectTreasury treasury,
        IBlockchainProviderFactory providers,
        IIdempotencyStore idempotency,
        ILogger<ReserveToTreasuryBuyBackBackstop>? logger = null)
    {
        _reserve = reserve ?? throw new ArgumentNullException(nameof(reserve));
        _treasury = treasury ?? throw new ArgumentNullException(nameof(treasury));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<BuyBackReservation>> ReserveAsync(
        BuyBackTopUpRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ShortfallBaseUnits <= BigInteger.Zero)
        {
            return AZOAResult<BuyBackReservation>.Success(
                new BuyBackReservation(BigInteger.Zero), "There was nothing to set aside.");
        }

        // A HOLD, not an issue: the units are spoken for but have not left, and the reserve's own
        // arithmetic refuses the draw outright once the partition is committed. That refusal is
        // the whole control - it is what stops one declared budget backing two sellers.
        var held = await _reserve.DrawAsync(
            new ReserveDrawRequest(
                request.Scope.NormalizedChainType,
                request.PlatformAssetId,
                request.Partition,
                ReserveDrawKind.Hold,
                request.ShortfallBaseUnits,
                PhaseKey(request, SetAsidePhase),
                ReserveDrawAuthority.NodeOperator),
            ct);

        if (held.IsError || held.Result is null)
        {
            _logger?.LogInformation(
                "The buy-back backstop could not set aside {Amount} base units on partition "
                + "{Partition}: {Detail}",
                request.ShortfallBaseUnits, request.Partition, held.Message);
            return AZOAResult<BuyBackReservation>.FailureWithCode(
                UnavailableMessage, ConsumerSafeErrorCodes.InsufficientFunds);
        }

        return AZOAResult<BuyBackReservation>.Success(
            new BuyBackReservation(request.ShortfallBaseUnits),
            "The reserve is holding these units for this request.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// The move is claimed ONCE per request and is deliberately not re-attempted after a rail
    /// refusal: the set-aside is still held, so a second attempt would send a second shortfall
    /// against one promise. The seller loses nothing by it - their place and their promise both
    /// stand, and the place still fills the moment the project's own funds cover it, which is the
    /// path that gives the set-aside back. What a refused movement needs is an operator, not a
    /// retry loop.
    /// <para>Exact saved wire: the transfer is prepared and saved once, then only observed or
    /// rebroadcast on every later call for the same request. A preparation refusal or an
    /// authoritative no-effect fails the claim and leaves the set-aside releasable; a CONFIRMED
    /// transfer converts the hold and credits the project; anything else keeps the claim in
    /// progress and the set-aside frozen (<see cref="ReleaseReservationAsync"/>).</para>
    /// </remarks>
    public async Task<AZOAResult<BuyBackTopUp>> TopUpAsync(
        BuyBackTopUpRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ShortfallBaseUnits <= BigInteger.Zero)
        {
            return AZOAResult<BuyBackTopUp>.Success(
                new BuyBackTopUp(BigInteger.Zero), "There was nothing to move.");
        }

        var chain = request.Scope.NormalizedChainType;
        var reference = new ProjectTreasuryReference(
            chain, request.Scope.TenantId, request.Scope.NormalizedProjectReference);

        var reserveAccount = await _reserve.GetAccountAsync(chain, ct);
        if (reserveAccount.IsError || reserveAccount.Result is null)
            return Unavailable(reserveAccount.Message);

        // FindAsync, never ResolveAsync: paying into a treasury must not be the thing that brings
        // one into existence.
        var treasuryAccount = await _treasury.FindAsync(reference, ct);
        if (treasuryAccount.IsError || treasuryAccount.Result is null)
            return Unavailable(treasuryAccount.Message);

        if (treasuryAccount.Result.Status != ProjectTreasuryStatus.Open)
            return Unavailable("The project's account is closed, so nothing was moved into it.");

        if (!TryGetProvider(chain, out var provider, out var providerError))
            return Unavailable(providerError);

        // The set-aside is NOT converted before the move, and that ordering is load-bearing. A
        // held unit and an issued unit reduce the partition's availability identically, so
        // moving first opens no oversubscription window at all - while converting first would
        // leave a failed move holding a promise the row still claims to be carrying.
        if (_idempotency is not IValueMoveReceiptStore receipts
            || !provider.TryGetModule<IValueTransferTransactionModule>(out var module) || module is null)
            return Unavailable("This rail has no durable prepared transfer, so the reserve cannot back this request.");
        var moveKey = PhaseKey(request, MovePhase);
        var claim = await _idempotency.TryClaimAsync(moveKey, OperationType, ct);
        if (!claim.Won)
        {
            // Only a COMPLETED claim proves the units reached the treasury. An in-progress claim
            // resumes its own saved bytes below; a failed one stays failed (one move per request).
            if (claim.Record.State == IdempotencyState.Completed)
                return AZOAResult<BuyBackTopUp>.Success(
                    new BuyBackTopUp(request.ShortfallBaseUnits),
                    "The reserve had already backed this request.");
            if (claim.Record.State != IdempotencyState.InProgress || claim.Record.OperationType != OperationType)
                return Unavailable("A backstop movement for this request did not complete and needs an operator.");
        }

        // Durable exact-wire transfer (Services/Reserve/AGENTS.md §three-outcomes): prepare
        // and save signed bytes once, then only observe or rebroadcast those bytes.
        var quantity = request.ShortfallBaseUnits.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var encoded = await receipts.GetPreparedAsync(moveKey, ct);
        if (encoded is null)
        {
            var generation = await receipts.GetPreparationGenerationAsync(moveKey, ct);
            var prepared = await module.PrepareValueTransferAsync(new ValueTransferIntent(chain, _providers.DefaultNetwork,
                request.PlatformAssetId, reserveAccount.Result.Address, treasuryAccount.Result.Address, quantity,
                SigningContext.PlatformFor(chain), $"value:{moveKey}:{generation}"), ct);
            if (prepared.IsError || prepared.Result is null)
            {
                // Never signed, never saved: the set-aside is untouched and may still be handed back.
                await _idempotency.FailAsync(moveKey, prepared.Message, ct);
                return Unavailable(prepared.Message);
            }
            await receipts.TryPrepareAsync(moveKey, JsonSerializer.Serialize(prepared.Result), ct, generation);
            encoded = await receipts.GetPreparedAsync(moveKey, ct);
        }
        var durable = encoded is null ? null : JsonSerializer.Deserialize<PreparedValueTransfer>(encoded);
        if (durable is null || durable.Intent.ChainType != chain || durable.Intent.Network != _providers.DefaultNetwork
            || durable.Intent.AssetId != request.PlatformAssetId || durable.Intent.Sender != reserveAccount.Result.Address
            || durable.Intent.Recipient != treasuryAccount.Result.Address || durable.Intent.QuantityBaseUnits != quantity)
            return Unavailable("The saved backstop movement does not match this request.");

        var observed = await module.ObserveValueTransferAsync(durable, ct);
        if (!observed.IsError && observed.Result == ChainConfirmation.Unknown)
        {
            try { await module.SubmitValueTransferAsync(durable, ct); }
            catch (HttpRequestException exception) { _logger?.LogWarning(exception, "Backstop rebroadcast for {OperationKey} did not reach the rail.", request.OperationKey); }
            observed = await module.ObserveValueTransferAsync(durable, ct);
        }
        if (!observed.IsError && observed.Result == ChainConfirmation.FailedOnChain)
        {
            // Authoritative no-effect: the units never left, so the claim fails plainly and the set-aside may be handed back.
            await _idempotency.FailAsync(moveKey, "The saved backstop movement is authoritatively unable to take effect.", ct);
            return Unavailable("The saved backstop movement is authoritatively unable to take effect.");
        }
        if (observed.IsError || observed.Result != ChainConfirmation.Confirmed)
        {
            // BROADCAST BUT UNCONFIRMED IS NOT LANDED. The claim stays in progress with its saved
            // bytes, the hold stays a hold, and ReleaseReservationAsync refuses to hand it back.
            _logger?.LogWarning(
                "A buy-back backstop movement for {OperationKey} (rail reference {Reference}) is saved and not yet "
                + "confirmed; the set-aside on partition {Partition} stays HELD until it settles.",
                request.OperationKey, durable.TransactionId, request.Partition);
            return Unavailable(AmbiguousMoveReason);
        }

        // The units have landed, so the set-aside becomes issued. A failure here leaves them
        // counted as held rather than issued, which changes nothing about the partition's
        // AVAILABILITY - it is a reconciliation job, never an oversubscription.
        var released = await _reserve.DrawAsync(
            new ReserveDrawRequest(
                chain,
                request.PlatformAssetId,
                request.Partition,
                ReserveDrawKind.ReleaseHold,
                request.ShortfallBaseUnits,
                PhaseKey(request, FillPhase),
                ReserveDrawAuthority.NodeOperator),
            ct);

        if (released.IsError)
        {
            _logger?.LogError(
                "A buy-back backstop movement for {OperationKey} settled but its set-aside is still "
                + "recorded as held: {Detail}",
                request.OperationKey, released.Message);
        }

        // The project's own counter, after the move. A failure here leaves the project holding
        // units its ledger has not counted yet, which is a reconciliation job and never a reason
        // to send them again - so the claim is completed either way.
        var credited = await _treasury.RecordMovementAsync(
            new ProjectTreasuryMovementRequest(
                reference,
                request.PlatformAssetId,
                ProjectTreasuryMovementKind.Credit,
                request.ShortfallBaseUnits,
                moveKey),
            ct);

        if (credited.IsError)
        {
            _logger?.LogError(
                "A buy-back backstop movement for {OperationKey} settled but the project's counter "
                + "was not updated: {Detail}",
                request.OperationKey, credited.Message);
        }

        await _idempotency.CompleteAsync(moveKey, durable.TransactionId, ct);

        return AZOAResult<BuyBackTopUp>.Success(
            new BuyBackTopUp(request.ShortfallBaseUnits), "The reserve backed this request.");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> ReleaseReservationAsync(
        BuyBackTopUpRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ShortfallBaseUnits <= BigInteger.Zero)
            return AZOAResult<bool>.Success(false, "Nothing was set aside for this request.");

        // The other half of the pending-movement control. A set-aside whose movement was
        // broadcast and never established may have LEFT the reserve on chain; handing the
        // partition its availability back would let the node promise those units to a second
        // seller while they sit in a project's treasury. Refusing here is the safe answer, and
        // the queue treats a refused give-back exactly as it treats any other: the place keeps
        // its promise until a person resolves it.
        // A saved in-flight transfer is resumed first: if it has landed, the set-aside is spent, not owed back.
        var moveClaim = await _idempotency.GetAsync(PhaseKey(request, MovePhase), ct);
        if (moveClaim is { State: IdempotencyState.InProgress, OperationType: OperationType }
            && await TopUpAsync(request, ct) is { IsError: false })
            return AZOAResult<bool>.Success(false, "The set-aside was already spent into the project's account; nothing was handed back.");

        if (await IsMoveAmbiguousAsync(request, ct))
        {
            _logger?.LogError(
                "MANUAL INTERVENTION REQUIRED — refused to hand back {Amount} base units on "
                + "partition {Partition} for {OperationKey}: its movement was submitted and never "
                + "established, so the units may already be the project's.",
                request.ShortfallBaseUnits, request.Partition, request.OperationKey);

            return AZOAResult<bool>.Failure(AmbiguousMoveReason);
        }

        var cancelled = await _reserve.DrawAsync(
            new ReserveDrawRequest(
                request.Scope.NormalizedChainType,
                request.PlatformAssetId,
                request.Partition,
                ReserveDrawKind.CancelHold,
                request.ShortfallBaseUnits,
                PhaseKey(request, ReleasePhase),
                ReserveDrawAuthority.NodeOperator),
            ct);

        if (cancelled.IsError)
        {
            // Refusing to cancel a hold the partition is not holding is the SAFE failure: it means
            // the units were already given back or already spent, and cancelling again would hand
            // the partition somebody else's set-aside.
            _logger?.LogWarning(
                "The buy-back backstop did not give back {Amount} base units for {OperationKey}: {Detail}",
                request.ShortfallBaseUnits, request.OperationKey, cancelled.Message);
            return AZOAResult<bool>.Failure(cancelled.Message);
        }

        return AZOAResult<bool>.Success(true, "The set-aside was given back to the partition.");
    }

    /// <summary>One draw key per phase of one request, so each phase replays exactly once.</summary>
    private static string PhaseKey(BuyBackTopUpRequest request, string phase) =>
        $"{request.OperationKey}:{phase}";

    /// <summary>
    /// True when this request's movement claim records an unestablished outcome. An unreadable
    /// ledger answers TRUE: not being able to prove the movement is settled is the same reason to
    /// refuse a give-back as knowing it is not.
    /// </summary>
    private async Task<bool> IsMoveAmbiguousAsync(BuyBackTopUpRequest request, CancellationToken ct)
    {
        try
        {
            var record = await _idempotency.GetAsync(PhaseKey(request, MovePhase), ct);
            if (record is null)
                return false;

            // An unfinished claim may hold saved bytes that still land; a legacy ambiguous failure may already have.
            if (record.State == IdempotencyState.InProgress)
                return true;
            return record.State == IdempotencyState.Failed
                && (record.Error ?? string.Empty).Contains(AmbiguousMoveReason, StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogError(
                exception,
                "The buy-back backstop could not read the movement claim for {OperationKey}, so it "
                + "refused to hand the set-aside back.",
                request.OperationKey);
            return true;
        }
    }

    private bool TryGetProvider(string chainType, out IBlockchainProvider provider, out string error)
    {
        provider = null!;
        error = string.Empty;
        try
        {
            provider = _providers.GetProvider(chainType, _providers.DefaultNetwork);
            return true;
        }
        catch (ValueRailProviderNotFoundException)
        {
            error = "This node cannot move funds on that rail, so the platform could not stand behind it.";
            return false;
        }
    }

    private AZOAResult<BuyBackTopUp> Unavailable(string? detail)
    {
        if (!string.IsNullOrWhiteSpace(detail))
            _logger?.LogInformation("The buy-back backstop did not move anything: {Detail}", detail);

        return AZOAResult<BuyBackTopUp>.FailureWithCode(
            UnavailableMessage, ConsumerSafeErrorCodes.TemporarilyUnavailable);
    }
}
