// SPDX-License-Identifier: UNLICENSED
//
// OI-44 / OI-60. The buy-back backstop, as a movement the code makes rather than a seam it
// declares. Every phase moves the counter the promise is made against, so a promise this class
// did not manage to book simply does not exist.
//
// See Services/Reserve/AGENTS.md §buy-back.

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
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
        var moveKey = PhaseKey(request, MovePhase);
        var claim = await _idempotency.TryClaimAsync(moveKey, OperationType, ct);
        if (!claim.Won)
        {
            // Only a COMPLETED claim proves the units reached the treasury. Anything else may
            // still be in flight, and sending over an unknown outcome is how one budget pays
            // twice - so an unfinished claim is reported as "not moved" and the seller keeps a
            // place the reserve is still holding units for.
            return claim.Record.State == IdempotencyState.Completed
                ? AZOAResult<BuyBackTopUp>.Success(
                    new BuyBackTopUp(request.ShortfallBaseUnits),
                    "The reserve had already backed this request.")
                : Unavailable("A backstop movement for this request is already under way.");
        }

        var moved = await provider.TransferAsync(
            request.PlatformAssetId,
            reserveAccount.Result.Address,
            treasuryAccount.Result.Address,
            RailAmount.OfChainAsset(request.ShortfallBaseUnits, request.PlatformAssetId),
            SigningContext.PlatformFor(chain),
            ct);

        if (moved.IsError)
        {
            await _idempotency.FailAsync(moveKey, moved.Message, ct);

            // Nothing was converted, so the set-aside is exactly as it was: still held, still
            // backing the seller's place, and still what the queue row says it is.
            if (!string.IsNullOrWhiteSpace(moved.Result))
            {
                _logger?.LogError(
                    "A buy-back backstop movement for {OperationKey} did not confirm; it needs an "
                    + "operator reconciliation before the set-aside is trusted either way.",
                    request.OperationKey);
            }

            return Unavailable(moved.Message);
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

        await _idempotency.CompleteAsync(moveKey, moved.Result ?? string.Empty, ct);

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
