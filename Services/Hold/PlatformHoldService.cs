// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;   // SurrealTransientConflict (retryable 3.x txn conflicts)
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>
/// The HOLD primitive over the platform reserve's named-partition ledger. See
/// <c>Services/Hold/IPlatformHold.cs</c> for what a hold is and
/// <c>Services/Hold/AGENTS.md</c> for why the holding account is the reserve account.
/// </summary>
public sealed class PlatformHoldService : IPlatformHold
{
    /// <summary>Largest page any list call will return.</summary>
    public const int MaxListLimit = 500;

    /// <summary>Attempts a transition makes before giving up on a compare-and-swap race.</summary>
    private const int MaxCasAttempts = 4;

    private readonly IPlatformReserve _reserve;
    private readonly ICustodialRecipientPreparer _recipientPreparer;
    private readonly IBlockchainProviderFactory _providerFactory;
    private readonly IWalletStore _walletStore;
    private readonly IHoldLedgerStore _ledgerStore;
    private readonly ReserveOptions _reserveOptions;
    private readonly ILogger<PlatformHoldService> _logger;

    public PlatformHoldService(
        IPlatformReserve reserve,
        ICustodialRecipientPreparer recipientPreparer,
        IBlockchainProviderFactory providerFactory,
        IWalletStore walletStore,
        IHoldLedgerStore ledgerStore,
        ReserveOptions reserveOptions,
        ILogger<PlatformHoldService> logger)
    {
        _reserve = reserve ?? throw new ArgumentNullException(nameof(reserve));
        _recipientPreparer = recipientPreparer ?? throw new ArgumentNullException(nameof(recipientPreparer));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _walletStore = walletStore ?? throw new ArgumentNullException(nameof(walletStore));
        _ledgerStore = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));
        _reserveOptions = reserveOptions ?? throw new ArgumentNullException(nameof(reserveOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Open ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AZOAResult<HoldOutcome>> OpenAsync(
        HoldOpenRequest request, CancellationToken ct = default)
    {
        if (request is null)
            return Fail("A hold request is required.");

        var guard = GuardReference(request.Reference);
        if (guard is not null)
            return Fail(guard);

        // Funder-sourced holds are declared but NOT built: moving a person's units in
        // needs an idempotency anchor on the inbound leg, and without one a retried open
        // would charge the funder twice. Refusing is the honest answer; silently treating
        // it as reserve-sourced would issue node units against a payment nobody made.
        if (request.FundingSource != HoldFundingSource.Reserve)
            return Fail(
                "Only reserve-sourced holds are available on this node. A hold funded from a person's own " +
                "account needs an idempotent inbound value operation, which this service does not route " +
                "through yet; nothing was held.");

        if (request.Amount <= BigInteger.Zero)
            return Fail("A hold amount must be positive.");
        if (request.BeneficiaryUserId == Guid.Empty)
            return Fail("A hold needs the person it is held for.");
        if (request.FunderUserId != Guid.Empty)
            return Fail(
                "A reserve-sourced hold has no funding person: the units come from the platform reserve, " +
                "so naming a funder would record a payment that never happened.");

        var assetId = Normalize(request.AssetId);
        if (assetId.Length == 0)
            return Fail("A hold needs the asset it holds.");

        var partition = Normalize(request.ReservePartition);
        if (partition.Length == 0)
            return Fail(
                "A reserve-sourced hold must name the declared partition it draws from; an undeclared draw " +
                "is exactly what the partition ledger exists to refuse.");

        var chain = request.Reference.NormalizedChainType;
        var holdId = request.Reference.NormalizedHoldId;

        var account = await _reserve.GetAccountAsync(chain, ct);
        if (account.IsError || account.Result is null)
            return Fail($"No holding account is available on '{chain}': {account.Message}");

        // The holding account can only hold an asset the reserve actually holds. This is
        // the control that replaces "give the holding account its own acceptance of every
        // asset": the reserve is the manager/reserve authority on the assets this node
        // creates, so a registered asset needs no preparation — and an asset with no
        // reserve row is one the holding account may well be unable to receive at all.
        var reserveLedger = await _reserve.GetLedgerAsync(chain, assetId, ct);
        if (reserveLedger.IsError || reserveLedger.Result is null)
            return Fail(
                $"The platform reserve has no record of asset '{assetId}' on '{chain}', so this node cannot " +
                $"hold it. Register the asset with the reserve first; nothing was held.");

        // Cross-tenant control: a partition declared by one tenant may not be drawn by a
        // hold driven by another, and the platform's OWN unit — which carries no tenant at
        // all — is the operator's, not every tenant's. Without it, tenant B's action could
        // quietly consume the budget tenant A declared out of the shared reserve.
        var drawAuthority = request.ResolvedDrawAuthority;
        var tenantGuard = ReservePartitionScope.Refuse(
            reserveLedger.Result, drawAuthority, assetId, _reserveOptions);
        if (tenantGuard is not null)
            return Fail(tenantGuard);

        var now = DateTimeOffset.UtcNow;
        var row = new HoldLedger
        {
            Id = RecordIdFor(chain, holdId),
            HoldId = holdId,
            Chain = chain,
            Network = Network,
            HoldingAddress = account.Result.Address,
            AssetId = assetId,
            Amount = request.Amount.ToString(CultureInfo.InvariantCulture),
            ReservePartition = partition,
            FundingSource = HoldFundingSource.Reserve.ToString(),
            FunderUserId = null,
            BeneficiaryUserId = request.BeneficiaryUserId.ToString("N"),
            TenantId = request.ActingTenantId is { } tenant && tenant != Guid.Empty
                ? tenant.ToString("N")
                : null,
            Purpose = string.IsNullOrWhiteSpace(request.Purpose) ? null : request.Purpose.Trim(),
            State = HoldState.Pending.ToString(),
            ReleaseNotBefore = request.ReleaseNotBefore,
            AutoRelease = request.AutoRelease,
            InheritsReserveCommitment = false,
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var claim = await _ledgerStore.CreateIfAbsentAsync(row, ct);
        if (claim.IsError || claim.Result is null)
            return Fail(claim.Message);

        var persisted = claim.Result.Row;

        if (!claim.Result.Created)
        {
            // A retried open must return what the first one produced. DIFFERENT terms
            // under the same id are refused: reinterpreting them would let a caller
            // rewrite an existing commitment by replaying its id.
            var mismatch = DescribeTermsMismatch(persisted, row);
            if (mismatch is not null)
                return Fail(mismatch);

            // The first attempt may have died between the claim and the draw. Finish it
            // rather than returning a permanently Pending hold.
            if (ParseState(persisted.State) != HoldState.Pending)
                return Ok(persisted, null, null, replayed: true, "This hold was already open.");
        }

        return await CommitOpeningDrawAsync(persisted, request.Amount, drawAuthority, ct);
    }

    /// <summary>
    /// Draws the commitment out of the named partition and moves the row to
    /// <see cref="HoldState.Held"/>. The draw is keyed on the hold id, so finishing a
    /// half-opened hold replays rather than double-counting the partition.
    /// </summary>
    private async Task<AZOAResult<HoldOutcome>> CommitOpeningDrawAsync(
        HoldLedger row, BigInteger amount, ReserveDrawAuthority authority, CancellationToken ct)
    {
        // The OPENING draw carries the opener's own authority, not a settlement pass: the
        // asset and the partition on this row arrived off the caller's request.
        var draw = await _reserve.DrawAsync(
            new ReserveDrawRequest(
                row.Chain,
                row.AssetId,
                row.ReservePartition,
                ReserveDrawKind.Hold,
                amount,
                OperationKey("hold", row.HoldId),
                authority),
            ct);

        if (draw.IsError || draw.Result is null)
        {
            _logger.LogWarning(
                "Hold {HoldId} refused by reserve partition '{Partition}' for asset {Asset}: {Detail}",
                row.HoldId, row.ReservePartition, row.AssetId, draw.Message);

            // The row exists but holds nothing, and it must never look releasable. Marking
            // it Refunded is the truthful terminal state: no commitment was taken and none
            // is owed back.
            var voided = await TransitionAsync(
                row,
                HoldState.Refunded,
                mutate: candidate =>
                {
                    candidate.SettlementReason =
                        "the reserve partition refused the commitment; nothing was ever held";
                    candidate.SettledAt = DateTimeOffset.UtcNow;
                },
                ct: ct);

            return Fail(draw.Message, voided.Result);
        }

        var held = await TransitionAsync(row, HoldState.Held, mutate: null, ct: ct);
        if (held.IsError || held.Result is null)
            return Fail(held.Message);

        return Ok(held.Result, null, null, replayed: false, "Hold opened.");
    }

    // ── Release ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AZOAResult<HoldOutcome>> ReleaseAsync(
        HoldSettlementRequest request, CancellationToken ct = default)
    {
        if (request is null)
            return Fail("A hold settlement request is required.");

        var current = await GetAsync(request.Reference, ct);
        if (current.IsError || current.Result is null)
            return Fail(current.Message);

        var row = current.Result;
        var state = ParseState(row.State);

        if (state == HoldState.Released)
            return Ok(row, null, row.SettlementTransactionId, replayed: true,
                "This hold was already released.");

        if (state is not (HoldState.Held or HoldState.Releasing))
            return Fail(RefuseTransition(row, state, "released"));

        // The hold window. This single check is what makes "your stake becomes yours in
        // N days" true rather than a sentence in a terms page.
        if (row.ReleaseNotBefore is { } notBefore && DateTimeOffset.UtcNow < notBefore)
            return Fail(
                $"Hold '{row.HoldId}' cannot be released before {notBefore:u}; its window has not elapsed. " +
                "Nothing was moved.");

        if (!BigInteger.TryParse(
                row.Amount, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount)
            || amount <= BigInteger.Zero)
        {
            return Fail($"Hold '{row.HoldId}' carries an unreadable amount; refusing to release it.");
        }

        var beneficiary = ParseUserId(row.BeneficiaryUserId);
        if (beneficiary == Guid.Empty)
            return Fail($"Hold '{row.HoldId}' names no readable beneficiary; refusing to release it.");

        // A release already broadcast is reconciled against the rail, never re-sent: the
        // second send would be a second payment out of the holding account.
        if (!string.IsNullOrWhiteSpace(row.SettlementTransactionId))
        {
            var reconciled = await ReconcileBroadcastReleaseAsync(row, ct);
            if (reconciled is not null)
                return reconciled;
        }

        var wallet = await FindWalletAsync(beneficiary, row.Chain);
        if (wallet.Wallet is null)
            return Fail(wallet.Error);

        var contexts = BuildContexts(row, beneficiary, wallet.Wallet.Id, request.ActingTenantId);

        // Recipient preparation. Without it the first release to a worker who has never
        // held this asset fails on the rail, naming the asset and its acceptance step —
        // exactly the blockchain error a non-crypto worker must never see.
        var prepared = await _recipientPreparer.PrepareAsync(
            new CustodialRecipientPreparation(
                row.Chain,
                row.AssetId,
                wallet.Wallet.Address,
                contexts.Beneficiary,
                row.HoldingAddress,
                contexts.Holding),
            ct);
        if (prepared.IsError || prepared.Result is null)
            return Fail(prepared.Message);

        if (prepared.Result.PendingConfirmation)
            return AZOAResult<HoldOutcome>.FailureWithCode(
                "The recipient's account is being made ready and has not finished settling. " +
                "Retry shortly; nothing was moved.",
                ConsumerSafeErrorCodes.SettlementPending);

        // The partition draw commits BEFORE the move, and the row parks in Releasing so a
        // refund cannot take back units the ledger has already counted as issued. The draw
        // is keyed on the hold id, so a retry replays instead of double-counting. Exactly
        // one row in a parent/child chain reaches here, because chaining leaves the parent
        // terminal in ReleasedIntoHold.
        var draw = await _reserve.DrawAsync(
            new ReserveDrawRequest(
                row.Chain,
                row.AssetId,
                row.ReservePartition,
                ReserveDrawKind.ReleaseHold,
                amount,
                OperationKey("release", row.HoldId),
                // Settling a commitment the opening draw already scope-checked: chain, asset
                // and partition all come off this persisted row, so no caller names them.
                ReserveDrawAuthority.SettlingGuardedCommitment),
            ct);
        if (draw.IsError || draw.Result is null)
            return Fail(draw.Message);

        if (state != HoldState.Releasing)
        {
            var releasing = await TransitionAsync(row, HoldState.Releasing, mutate: null, ct: ct);
            if (releasing.IsError || releasing.Result is null)
                return Fail(releasing.Message);
            row = releasing.Result;
        }

        var provider = ResolveProvider(row.Chain, out var providerError);
        if (provider is null)
            return Fail(providerError);

        var moved = await provider.TransferAsync(
            row.AssetId,
            row.HoldingAddress,
            wallet.Wallet.Address,
            RailAmount.OfChainAsset(amount, row.AssetId),
            contexts.Holding,
            ct);

        if (moved.IsError || string.IsNullOrWhiteSpace(moved.Result))
        {
            _logger.LogWarning(
                "Hold {HoldId} could not be paid out to its beneficiary: {Detail}",
                row.HoldId, moved.Message);

            // A broadcast-then-timeout still produces a rail reference. Persisting it even
            // on the failure path is what lets the retry RECONCILE instead of re-sending;
            // discarding it is the double-payment hole this codebase has closed twice
            // before on the mint path.
            if (!string.IsNullOrWhiteSpace(moved.Result))
            {
                await TransitionAsync(
                    row,
                    HoldState.Releasing,
                    mutate: candidate => candidate.SettlementTransactionId = moved.Result,
                    ct: ct);
            }

            // The row stays in Releasing so the sweep finishes it, and the refusal is
            // mapped before it leaves the node: a raw rail message here is the one place a
            // sponsor would see an address, an asset id or the word "opt-in".
            return ConsumerSafeErrors.Failure<HoldOutcome>(moved.Message);
        }

        var settled = await TransitionAsync(
            row,
            HoldState.Released,
            mutate: candidate =>
            {
                candidate.SettlementTransactionId = moved.Result;
                candidate.SettledAt = DateTimeOffset.UtcNow;
                candidate.SettlementReason = string.IsNullOrWhiteSpace(request.Reason)
                    ? "the hold window elapsed and the units were paid to the beneficiary"
                    : request.Reason!.Trim();
            },
            ct: ct);

        if (settled.IsError || settled.Result is null)
        {
            // The move landed; only the bookkeeping lost its race. Say so precisely — a
            // caller that reads this as "nothing moved" would pay the beneficiary twice.
            _logger.LogError(
                "Hold {HoldId} paid out as {Transaction} but the ledger row could not be updated: {Detail}",
                row.HoldId, moved.Result, settled.Message);
            return Fail(
                $"Hold '{row.HoldId}' was paid out but its ledger row could not be updated. The units HAVE " +
                "moved; do not release it again.");
        }

        return Ok(settled.Result, null, moved.Result, replayed: false, "Hold released.");
    }

    /// <summary>
    /// Decides what a previously-broadcast release means before anything is re-sent.
    /// Returns null only when the rail positively says the earlier attempt did NOT land.
    /// </summary>
    private async Task<AZOAResult<HoldOutcome>?> ReconcileBroadcastReleaseAsync(
        HoldLedger row, CancellationToken ct)
    {
        var provider = ResolveProvider(row.Chain, out var providerError);
        if (provider is null)
            return Fail(providerError);

        var confirmation = await provider.GetTransactionConfirmationAsync(row.SettlementTransactionId!, ct);
        if (confirmation.IsError)
            return Fail(
                $"Hold '{row.HoldId}' has a payout in flight that could not be checked. Nothing was resent; " +
                "try again shortly.");

        switch (confirmation.Result)
        {
            case ChainConfirmation.Confirmed:
                var settled = await TransitionAsync(
                    row,
                    HoldState.Released,
                    mutate: candidate =>
                    {
                        candidate.SettledAt ??= DateTimeOffset.UtcNow;
                        candidate.SettlementReason ??= "the payout was confirmed on reconciliation";
                    },
                    ct: ct);
                return Ok(
                    settled.Result ?? row, null, row.SettlementTransactionId, replayed: true,
                    "This hold had already been paid out.");

            case ChainConfirmation.FailedOnChain:
                return null; // the earlier attempt did not land; resending is safe

            default:
                // Pending or Unknown. Never auto-act: a second send against a payout that
                // is merely unobserved is how one hold pays twice.
                return Fail(
                    $"Hold '{row.HoldId}' has a payout that has not settled yet. Nothing was resent; " +
                    "it will complete on its own.");
        }
    }

    // ── Refund ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AZOAResult<HoldOutcome>> RefundAsync(
        HoldSettlementRequest request, CancellationToken ct = default)
    {
        if (request is null)
            return Fail("A hold settlement request is required.");

        var current = await GetAsync(request.Reference, ct);
        if (current.IsError || current.Result is null)
            return Fail(current.Message);

        var row = current.Result;
        var state = ParseState(row.State);

        if (state == HoldState.Refunded)
            return Ok(row, null, null, replayed: true, "This hold was already given back.");

        // Releasing is deliberately excluded. Its units are already counted as issued by
        // the release draw, so "giving them back" here would credit the partition twice.
        if (state is not (HoldState.Pending or HoldState.Held))
            return Fail(RefuseTransition(row, state, "given back"));

        if (state == HoldState.Held)
        {
            if (!BigInteger.TryParse(
                    row.Amount, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount)
                || amount <= BigInteger.Zero)
            {
                return Fail($"Hold '{row.HoldId}' carries an unreadable amount; refusing to give it back.");
            }

            // The chain's ONE commitment is cancelled here, whether this row opened it or
            // inherited it. Only the OPENING draw is skipped by a child (it is created
            // already Held); a parent that was continued is terminal in ReleasedIntoHold
            // and can never reach this branch, so the cancel happens exactly once per
            // chain. Keying it on the settling hold's own id keeps a retry idempotent.
            var draw = await _reserve.DrawAsync(
                new ReserveDrawRequest(
                    row.Chain,
                    row.AssetId,
                    row.ReservePartition,
                    ReserveDrawKind.CancelHold,
                    amount,
                    OperationKey("refund", row.HoldId),
                    // As with release: the row, not a caller, names what is being given back.
                    ReserveDrawAuthority.SettlingGuardedCommitment),
                ct);
            if (draw.IsError || draw.Result is null)
                return Fail(draw.Message);
        }

        var refunded = await TransitionAsync(
            row,
            HoldState.Refunded,
            mutate: candidate =>
            {
                candidate.SettledAt = DateTimeOffset.UtcNow;
                candidate.SettlementReason = string.IsNullOrWhiteSpace(request.Reason)
                    ? "the hold was given back before it was released"
                    : request.Reason!.Trim();
            },
            ct: ct);

        if (refunded.IsError || refunded.Result is null)
            return Fail(refunded.Message);

        // Nothing moves on a refund BY CONSTRUCTION: a reserve-sourced hold's units never
        // left the holding account, so giving them back is a ledger fact, not a payment.
        return Ok(refunded.Result, null, null, replayed: false, "Hold given back.");
    }

    // ── Release into a second hold ────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AZOAResult<HoldOutcome>> ReleaseIntoHoldAsync(
        HoldChainRequest request, CancellationToken ct = default)
    {
        if (request is null)
            return Fail("A hold continuation request is required.");

        var nextHoldId = Normalize(request.NextHoldId);
        if (nextHoldId.Length == 0)
            return Fail("Continuing a hold needs an id for the hold that continues it.");
        if (request.NextBeneficiaryUserId == Guid.Empty)
            return Fail("Continuing a hold needs the person the units are held for next.");

        var current = await GetAsync(request.Reference, ct);
        if (current.IsError || current.Result is null)
            return Fail(current.Message);

        var parent = current.Result;
        var state = ParseState(parent.State);

        if (state == HoldState.ReleasedIntoHold)
        {
            if (!string.Equals(parent.ChildHoldId, nextHoldId, StringComparison.Ordinal))
                return Fail(
                    $"Hold '{parent.HoldId}' was already continued as '{parent.ChildHoldId}'; it cannot be " +
                    $"continued again as '{nextHoldId}'.");

            var existingChild = await _ledgerStore.GetAsync(RecordIdFor(parent.Chain, nextHoldId), ct);
            return Ok(parent, existingChild.Result, null, replayed: true,
                "This hold had already been continued.");
        }

        if (state != HoldState.Held)
            return Fail(RefuseTransition(parent, state, "continued as a second hold"));

        // The window control for a continuation. Releasing EARLY into another hold is
        // allowed — no value leaves the holding account — but the next window may never
        // be shorter than this one, or continuing a hold would be the way out of the
        // funder's window that a plain release is refused for.
        if (parent.ReleaseNotBefore is { } parentWindow)
        {
            if (request.ReleaseNotBefore is not { } childWindow)
                return Fail(
                    $"Hold '{parent.HoldId}' is held until {parentWindow:u}; the hold continuing it must keep " +
                    "at least that window, and this one has none.");

            if (childWindow < parentWindow)
                return Fail(
                    $"Hold '{parent.HoldId}' is held until {parentWindow:u}; the hold continuing it may not " +
                    $"end earlier ({childWindow:u}).");
        }

        var now = DateTimeOffset.UtcNow;
        var child = new HoldLedger
        {
            Id = RecordIdFor(parent.Chain, nextHoldId),
            HoldId = nextHoldId,
            Chain = parent.Chain,
            Network = parent.Network,
            HoldingAddress = parent.HoldingAddress,
            AssetId = parent.AssetId,
            Amount = parent.Amount,
            ReservePartition = parent.ReservePartition,
            FundingSource = parent.FundingSource,
            FunderUserId = parent.FunderUserId,
            BeneficiaryUserId = request.NextBeneficiaryUserId.ToString("N"),
            TenantId = request.ActingTenantId is { } tenant && tenant != Guid.Empty
                ? tenant.ToString("N")
                : parent.TenantId,
            Purpose = string.IsNullOrWhiteSpace(request.Purpose) ? parent.Purpose : request.Purpose.Trim(),
            State = HoldState.Held.ToString(),
            ReleaseNotBefore = request.ReleaseNotBefore,
            AutoRelease = request.AutoRelease,
            // The parent already drew this commitment out of the partition. The child
            // inherits it so exactly ONE row in the chain ever releases or cancels it.
            InheritsReserveCommitment = true,
            ParentHoldId = parent.HoldId,
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var claim = await _ledgerStore.CreateIfAbsentAsync(child, ct);
        if (claim.IsError || claim.Result is null)
            return Fail(claim.Message);

        if (!claim.Result.Created &&
            !string.Equals(claim.Result.Row.ParentHoldId, parent.HoldId, StringComparison.Ordinal))
        {
            return Fail(
                $"Hold id '{nextHoldId}' is already in use by a different hold; nothing was continued.");
        }

        var closed = await TransitionAsync(
            parent,
            HoldState.ReleasedIntoHold,
            mutate: candidate =>
            {
                candidate.ChildHoldId = nextHoldId;
                candidate.SettledAt = DateTimeOffset.UtcNow;
                candidate.SettlementReason = string.IsNullOrWhiteSpace(request.Purpose)
                    ? "the hold was continued as a second hold over the same units"
                    : request.Purpose!.Trim();
            },
            ct: ct);

        if (closed.IsError || closed.Result is null)
            return Fail(closed.Message);

        return Ok(closed.Result, claim.Result.Row, null, replayed: false,
            "Hold continued as a second hold.");
    }

    // ── Reads ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AZOAResult<HoldLedger>> GetAsync(
        HoldReference reference, CancellationToken ct = default)
    {
        var guard = GuardReference(reference);
        if (guard is not null)
            return AZOAResult<HoldLedger>.Failure(guard);

        var row = await _ledgerStore.GetAsync(
            RecordIdFor(reference.NormalizedChainType, reference.NormalizedHoldId), ct);
        if (row.IsError)
            return AZOAResult<HoldLedger>.Failure(row.Message);
        if (row.Result is null)
            return AZOAResult<HoldLedger>.Failure(
                $"No hold '{reference.NormalizedHoldId}' exists on '{reference.NormalizedChainType}'.");

        return AZOAResult<HoldLedger>.Success(row.Result, "Success");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<HoldLedger>>> ListForUserAsync(
        Guid userId, int limit = 100, CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
            return AZOAResult<IReadOnlyList<HoldLedger>>.Failure("A user is required to list holds.");

        var rows = await _ledgerStore.ListForUserAsync(userId.ToString("N"), ct);
        if (rows.IsError || rows.Result is null)
            return AZOAResult<IReadOnlyList<HoldLedger>>.Failure(rows.Message);

        return AZOAResult<IReadOnlyList<HoldLedger>>.Success(
            rows.Result.Take(Math.Clamp(limit, 1, MaxListLimit)).ToList(), "Success");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<HoldLedger>>> ListDueAsync(
        string chainType, DateTimeOffset asOf, int limit = 50, CancellationToken ct = default)
    {
        var chain = Normalize(chainType);
        if (chain.Length == 0)
            return AZOAResult<IReadOnlyList<HoldLedger>>.Failure("A chain is required to list due holds.");

        var rows = await _ledgerStore.ListDueAsync(
            chain, Network, asOf, Math.Clamp(limit, 1, MaxListLimit), ct);
        if (rows.IsError || rows.Result is null)
            return AZOAResult<IReadOnlyList<HoldLedger>>.Failure(rows.Message);

        return AZOAResult<IReadOnlyList<HoldLedger>>.Success(rows.Result, "Success");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string Network => _providerFactory.DefaultNetwork.ToString();

    private static string Normalize(string? text) => text?.Trim() ?? string.Empty;

    private string RecordIdFor(string chain, string holdId) =>
        HoldLedger.RecordIdFor(chain, Network, holdId);

    /// <summary>The reserve draw key for one stage of one hold; stable across retries.</summary>
    private static string OperationKey(string stage, string holdId) => $"hold:{stage}:{holdId}";

    /// <summary>Null when the reference can address a hold; the refusal message otherwise.</summary>
    private string? GuardReference(HoldReference? reference)
    {
        if (reference is null || !reference.IsComplete)
            return "A hold needs a chain and a hold id.";

        var chain = reference.NormalizedChainType;
        if (!IsChainApproved(chain))
            return
                $"Chain '{chain}' is not listed under {ReserveOptions.SectionName}:Chains, so this node holds " +
                "no value there. Add it deliberately before any value moves on that chain.";

        return null;
    }

    private bool IsChainApproved(string chain) =>
        _reserveOptions.Chains.Count == 0 ||
        _reserveOptions.Chains.Any(candidate =>
            string.Equals(candidate?.Trim(), chain, StringComparison.OrdinalIgnoreCase));

    /// <summary>Null when a replayed open carries the same terms; the refusal otherwise.</summary>
    private static string? DescribeTermsMismatch(HoldLedger persisted, HoldLedger requested)
    {
        if (!string.Equals(persisted.AssetId, requested.AssetId, StringComparison.Ordinal) ||
            !string.Equals(persisted.Amount, requested.Amount, StringComparison.Ordinal) ||
            !string.Equals(persisted.ReservePartition, requested.ReservePartition, StringComparison.Ordinal) ||
            !string.Equals(persisted.BeneficiaryUserId, requested.BeneficiaryUserId, StringComparison.Ordinal))
        {
            return
                $"Hold id '{persisted.HoldId}' already names a different hold. Replaying an id with different " +
                "terms would rewrite a live commitment; nothing was changed.";
        }

        return null;
    }

    private static string RefuseTransition(HoldLedger row, HoldState state, string verb) =>
        $"Hold '{row.HoldId}' is {state.ToString().ToLowerInvariant()} and cannot be {verb}.";

    /// <summary>The reserve's own context, plus the beneficiary's, both tenant-stamped when driven.</summary>
    private static (SigningContext Holding, SigningContext Beneficiary) BuildContexts(
        HoldLedger row, Guid beneficiaryUserId, Guid walletId, Guid? actingTenantId)
    {
        var tenant = actingTenantId ?? ParseUserId(row.TenantId);

        var holding = StampTenant(
            SigningContext.PlatformFor(row.Chain), tenant, beneficiaryUserId);
        var beneficiary = StampTenant(
            SigningContext.ForUser(beneficiaryUserId, walletId).ForChain(row.Chain),
            tenant,
            beneficiaryUserId);

        return (holding, beneficiary);
    }

    /// <summary>
    /// Threads the acting tenant, grantor and transfer scope onto a signing context so the
    /// custody seam runs the live consent check before any decrypt. A hold nobody is
    /// driving on someone's behalf needs no grant and is returned unchanged.
    /// </summary>
    private static SigningContext StampTenant(SigningContext context, Guid tenantId, Guid grantorUserId) =>
        tenantId != Guid.Empty
            ? context.ActingAs(tenantId, AzoaScopes.TransferSign, grantorUserId)
            : context;

    private IBlockchainProvider? ResolveProvider(string chain, out string error)
    {
        error = string.Empty;
        try
        {
            return _providerFactory.GetProvider(chain, _providerFactory.DefaultNetwork);
        }
        catch (ValueRailProviderNotFoundException)
        {
            error = $"No provider is registered for '{chain}', so this hold cannot be settled.";
            return null;
        }
    }

    private async Task<(IWallet? Wallet, string Error)> FindWalletAsync(Guid userId, string chain)
    {
        var wallets = await _walletStore.GetByUserAsync(userId);
        if (wallets.IsError)
            return (null, wallets.Message);

        var wallet = wallets.Result?.FirstOrDefault(candidate =>
            string.Equals(candidate.ChainType, chain, StringComparison.OrdinalIgnoreCase));

        return wallet is null
            ? (null, "This hold's beneficiary has no account on this node yet, so nothing can be paid out.")
            : (wallet, string.Empty);
    }

    /// <summary>
    /// Compare-and-swap transition. Re-reads before each attempt so a concurrent writer's
    /// row is the one mutated, and never blind-overwrites a state another caller reached.
    /// </summary>
    private async Task<AZOAResult<HoldLedger>> TransitionAsync(
        HoldLedger row, HoldState next, Action<HoldLedger>? mutate, CancellationToken ct)
    {
        var recordId = row.Id;

        for (var attempt = 1; attempt <= MaxCasAttempts; attempt++)
        {
            var fresh = await _ledgerStore.GetAsync(recordId, ct);
            if (fresh.IsError)
                return AZOAResult<HoldLedger>.Failure(fresh.Message);
            if (fresh.Result is null)
                return AZOAResult<HoldLedger>.Failure($"Hold row '{recordId}' vanished mid-transition.");

            var candidate = fresh.Result;
            if (ParseState(candidate.State) == next && mutate is null)
                return AZOAResult<HoldLedger>.Success(candidate, "Already in this state.");

            var expectedVersion = candidate.Version;
            candidate.State = next.ToString();
            mutate?.Invoke(candidate);
            candidate.Version = expectedVersion + 1;
            candidate.UpdatedAt = DateTimeOffset.UtcNow;

            var saved = await _ledgerStore.UpdateIfVersionAsync(candidate, expectedVersion, ct);
            if (!saved.IsError && saved.Result is not null)
                return AZOAResult<HoldLedger>.Success(saved.Result, "Saved.");

            if (attempt == MaxCasAttempts)
                return AZOAResult<HoldLedger>.Failure(saved.Message);
        }

        return AZOAResult<HoldLedger>.Failure("The hold transition lost every compare-and-swap attempt.");
    }

    /// <summary>An unreadable state is treated as terminal, never as releasable.</summary>
    private static HoldState ParseState(string? state) =>
        Enum.TryParse<HoldState>(state, ignoreCase: true, out var parsed) ? parsed : HoldState.Refunded;

    /// <summary>A user id off a row; a record link keeps its <c>user:</c> prefix, so strip it.</summary>
    private static Guid ParseUserId(string? userId) => ReservePartitionScope.ParseRowUserId(userId);

    private static AZOAResult<HoldOutcome> Fail(string message, HoldLedger? row = null) =>
        AZOAResult<HoldOutcome>.Failure(
            message, row is null ? null : new HoldOutcome(row, null, null, Replayed: false));

    private static AZOAResult<HoldOutcome> Ok(
        HoldLedger hold, HoldLedger? next, string? transactionId, bool replayed, string message) =>
        AZOAResult<HoldOutcome>.Success(new HoldOutcome(hold, next, transactionId, replayed), message);
}

/// <summary>
/// SurrealDB-backed hold ledger. Colocated with the service because
/// <c>Providers/Stores/Surreal/</c> is outside this change's file boundary; the seam is
/// <see cref="IHoldLedgerStore"/>, so moving it later is a file move.
/// </summary>
public sealed class SurrealHoldLedgerStore : IHoldLedgerStore
{
    private const string Table = HoldLedger.SchemaNameConst;

    private readonly ISurrealExecutor _executor;

    public SurrealHoldLedgerStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<HoldLedger?>> GetAsync(string recordId, CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery<HoldLedger>.Key(recordId);
            var row = await _executor.QuerySingleAsync<HoldLedger>(query, ct);
            return new AZOAResult<HoldLedger?>
            {
                Result = row,
                Message = row is null ? "Not found." : "Success",
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<HoldLedger?>().CaptureException(ex, $"Hold ledger read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<HoldLedgerClaim>> CreateIfAbsentAsync(
        HoldLedger row, CancellationToken ct = default)
    {
        try
        {
            var create = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", row.Id)
                .WithParam("_body", row);

            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(create, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);

            var created = response.GetValues<HoldLedger>(0).SingleOrDefault() ?? row;
            return AZOAResult<HoldLedgerClaim>.Success(new HoldLedgerClaim(created, Created: true), "Created.");
        }
        catch (Exception ex)
        {
            // A CREATE that lost to an existing row is the idempotent case, not a fault —
            // but only when the row really is there now. Re-read before concluding that,
            // so a transport failure is never reported as "this hold already exists".
            var existing = await GetAsync(row.Id, ct);
            if (!existing.IsError && existing.Result is not null)
                return AZOAResult<HoldLedgerClaim>.Success(
                    new HoldLedgerClaim(existing.Result, Created: false), "Already open.");

            return new AZOAResult<HoldLedgerClaim>()
                .CaptureException(ex, $"Hold ledger create failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<HoldLedger>> UpdateIfVersionAsync(
        HoldLedger row, long expectedVersion, CancellationToken ct = default)
    {
        try
        {
            // Single-winner conditional update (the ActionRun precedent): the write lands
            // only while the persisted version is still the one this caller reasoned over,
            // so two concurrent releases cannot both settle one hold.
            var update = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) CONTENT $_body WHERE version = $_expected RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", row.Id)
                .WithParam("_body", row)
                .WithParam("_expected", expectedVersion);

            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(update, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);

            if (response.Count == 0 || !response[0].IsOk || response[0].AffectedCount() != 1)
                return AZOAResult<HoldLedger>.Failure(
                    $"Hold ledger row '{row.Id}' changed underneath this update " +
                    $"(expected version {expectedVersion}).");

            var persisted = response.GetValues<HoldLedger>(0).SingleOrDefault() ?? row;
            return AZOAResult<HoldLedger>.Success(persisted, "Saved.");
        }
        catch (Exception ex)
        {
            return new AZOAResult<HoldLedger>()
                .CaptureException(ex, $"Hold ledger update failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<HoldLedger>>> ListForUserAsync(
        string userId, CancellationToken ct = default)
    {
        try
        {
            // beneficiary_user_id and funder_user_id are record<user> columns, so the
            // parameter has to be bound as a native record id — a bare string compares
            // equal to nothing and would return an empty list with no error at all.
            var query = SurrealQuery
                .Of("SELECT * FROM hold_ledger WHERE beneficiary_user_id = $_user OR funder_user_id = $_user ORDER BY created_at DESC")
                .WithParam("_user", SurrealRecordParam.Of("user", userId));

            var rows = await _executor.QueryAsync<HoldLedger>(query, ct);
            return AZOAResult<IReadOnlyList<HoldLedger>>.Success(
                rows?.ToList() ?? new List<HoldLedger>(), "Success");
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<HoldLedger>>()
                .CaptureException(ex, $"Hold ledger list failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<HoldLedger>>> ListDueAsync(
        string chain, string network, DateTimeOffset asOf, int limit, CancellationToken ct = default)
    {
        try
        {
            // Two work lists in one read: holds whose window has elapsed and that asked to
            // be released automatically, plus holds a crash left mid-release. The second
            // set carries no window condition on purpose — a half-released hold is overdue
            // by definition and must be finished, not waited on.
            var query = SurrealQuery
                .Of("SELECT * FROM hold_ledger WHERE chain = $_chain AND network = $_network AND ((state = 'Held' AND auto_release = true AND (release_not_before = NONE OR release_not_before <= $_as_of)) OR state = 'Releasing') ORDER BY release_not_before ASC LIMIT $_limit")
                .WithParam("_chain", chain)
                .WithParam("_network", network)
                .WithParam("_as_of", asOf)
                .WithParam("_limit", limit);

            var rows = await _executor.QueryAsync<HoldLedger>(query, ct);
            return AZOAResult<IReadOnlyList<HoldLedger>>.Success(
                rows?.ToList() ?? new List<HoldLedger>(), "Success");
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<HoldLedger>>()
                .CaptureException(ex, $"Due-hold list failed: {ex.Message}");
        }
    }
}
