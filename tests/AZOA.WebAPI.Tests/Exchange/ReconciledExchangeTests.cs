// SPDX-License-Identifier: UNLICENSED
//
// OI-33, both halves, asserted at the counters.
//
// ReserveExchangeManager stops short of two things when the rail merely ACCEPTS a group, and
// leaves them to chain truth: it does not credit the project, and it does not unwind. Until this
// wave nothing ever finished either — a BUY the submitter reported Accepted moved the sponsor's
// units on chain and NEVER credited the project on the node's own ledger, and a group that
// dropped after acceptance left the partition permanently drawn with no sweeper.
//
// Every assertion below reads what the project's ledger and the partition's counters actually
// hold, never that a collaborator was called.

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Exchange;

public sealed class ReconciledExchangeTests
{
    private const string Chain = "Algorand";
    private const string PlatformAsset = "1001";
    private const string ProjectAsset = "2002";
    private const string PartitionName = "investor";
    private const string ProjectReference = "project-alpha";
    private const string LedgerKey = "exchange:apikey:client-1";
    private const string ReserveAddress = "RESERVEADDRESS";

    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    // ── The credit a buy withheld until confirmation actually lands ───────────

    [Fact]
    public async Task A_confirmed_buy_whose_credit_was_outstanding_is_credited_exactly_once()
    {
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedBuy(creditApplied: false, platformAmount: 700, projectUnits: 350);

        var settled = await world.Reconciler().SettleConfirmedAsync(operation);

        settled.Should().BeTrue();
        world.TreasuryCredits.Should().ContainSingle().Which.Should().Be(
            new BigInteger(700),
            "the sponsor's units moved on chain; without this the project was never credited on "
            + "the node's own ledger, and that ledger is the availability control a later sell "
            + "is paid out against");
        operation.Parameters[ReserveExchangeManager.ProjectCreditAppliedParameter].Should().Be(
            "true", "the flag two read surfaces consult has to move with the money");

        // A second sweep must not credit again.
        (await world.Reconciler().SettleConfirmedAsync(operation)).Should().BeTrue();
        world.TreasuryCredits.Should().ContainSingle("a re-run is a replay, not a second credit");
    }

    [Fact]
    public async Task A_buy_whose_credit_still_cannot_be_recorded_is_refused_advancement()
    {
        var world = new ExchangeWorld(declaredPartition: 1000);
        world.TreasuryRefusesMovements();
        var operation = world.SeedBuy(creditApplied: false, platformAmount: 700, projectUnits: 350);

        var settled = await world.Reconciler().SettleConfirmedAsync(operation);

        settled.Should().BeFalse(
            "advancing it would settle the replay anchor over a credit that never landed, and a "
            + "settled anchor is never revisited");
        operation.Parameters[ReserveExchangeManager.ProjectCreditAppliedParameter].Should().Be(
            "false", "the row still states the truth: the credit is outstanding");
    }

    [Fact]
    public async Task A_sell_owes_no_project_credit_and_is_settled_untouched()
    {
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedSell(platformAmount: 700, projectUnits: 350);

        (await world.Reconciler().SettleConfirmedAsync(operation)).Should().BeTrue();

        world.TreasuryCredits.Should().BeEmpty("only a buy credits a project treasury");
    }

    // ── The bounded windows underneath are not idempotency ───────────────────

    [Fact]
    public async Task A_stale_flag_and_an_evicted_replay_key_do_not_credit_the_project_twice()
    {
        // The compound failure this reconciler is written to survive, and the one its own
        // bookkeeping used to create. The credit lands, the row write fails, so the row still says
        // the credit is OUTSTANDING and the next sweep re-enters. The only thing standing between
        // that sweep and a second credit was the treasury's replay window — 128 keys, evicted
        // oldest-first. Let 128 other movements hit that project in between and the window has
        // forgotten this key, so it applies the credit again: the project ends up funded twice for
        // one exchange, and that balance is what a later sell is paid out against.
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedBuy(creditApplied: false, platformAmount: 700, projectUnits: 350);

        world.OperationWritesFail();
        (await world.Reconciler().SettleConfirmedAsync(operation)).Should().BeFalse(
            "the row write failed, so the operation must not be advanced");
        world.TreasuryCredits.Should().ContainSingle(
            "the credit itself landed — that is exactly what makes the stale flag dangerous");

        // The row is exactly as the lost write left it, and the window has moved on.
        operation.Parameters[ReserveExchangeManager.ProjectCreditAppliedParameter] = "false";
        world.TreasuryForgetsReplayKeys();
        world.OperationWritesSucceed();

        var second = await world.Reconciler().SettleConfirmedAsync(operation);

        world.TreasuryCredits.Should().ContainSingle(
            "the money must not move a second time; the reconciler's own durable anchor says the "
            + "credit already landed, and it does not lean on somebody else's bounded window");
        world.TreasuryCredits.Single().Should().Be(new BigInteger(700));
        second.Should().BeTrue(
            "with the credit provably already applied and the row now writable, the exchange is "
            + "finished and may advance");
        operation.Parameters[ReserveExchangeManager.ProjectCreditAppliedParameter].Should().Be(
            "true", "the flag catches up with money that moved one sweep earlier");
    }

    // ── The settlement key space is the node's, not the caller's ─────────────

    [Fact]
    public async Task A_caller_cannot_reach_a_settlement_anchor_with_its_own_idempotency_key()
    {
        // An exchange ledger key is "exchange:{apiKeyId}:{clientIdempotencyKey}", so before the
        // anchors were namespaced a caller who sent Idempotency-Key "client-1:reconcile-credit"
        // produced the anchor of the exchange whose client key was "client-1", byte for byte. The
        // ledger's uniqueness is on the key ALONE, so the reconciler read the caller's Completed
        // row, called the credit already applied, and the project was never credited while the
        // node reported it settled. The prefix is what makes that key unreachable.
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedBuy(creditApplied: false, platformAmount: 700, projectUnits: 350);

        world.Anchors.SeedForeignCompleted(
            LedgerKey + ExchangeSettlementReconciler.ReconcileCreditAnchorSuffix,
            "reserve_exchange");

        var settled = await world.Reconciler().SettleConfirmedAsync(operation);

        settled.Should().BeTrue();
        world.TreasuryCredits.Should().ContainSingle().Which.Should().Be(
            new BigInteger(700),
            "a row a caller can author must not be mistaken for this reconciler's anchor");
    }

    [Fact]
    public void A_settlement_anchor_lives_where_no_caller_ledger_key_can_reach()
    {
        // The structural half of the fix, kept as a statement rather than a comment: a caller
        // controls only the TAIL of its own ledger key, so an anchor whose namespace LEADS is
        // unreachable no matter what the caller sends.
        var anchor = ExchangeSettlementReconciler.SettlementAnchor(
            LedgerKey, ExchangeSettlementReconciler.ReconcileCreditAnchorSuffix);

        anchor.Should().StartWith(ExchangeSettlementReconciler.SettlementAnchorPrefix);
        ExchangeSettlementReconciler.SettlementAnchorPrefix.Should().NotStartWith(
            "exchange:", "every caller-derived ledger key begins with the exchange namespace");
        anchor.Should().NotBe(
            LedgerKey + ExchangeSettlementReconciler.ReconcileCreditAnchorSuffix,
            "concatenating the suffix alone is the shape a caller could forge");
    }

    [Fact]
    public async Task A_settlement_anchor_held_by_a_foreign_record_refuses_instead_of_skipping()
    {
        // Defence in depth behind the prefix: if the key derivation ever moves again and a
        // stranger's row does land on an anchor, reading its Completed as "already applied" is
        // exactly how an unmade credit gets reported as handed back. A foreign holder refuses.
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedBuy(creditApplied: false, platformAmount: 700, projectUnits: 350);

        world.Anchors.SeedForeignCompleted(
            ExchangeSettlementReconciler.SettlementAnchor(
                LedgerKey, ExchangeSettlementReconciler.ReconcileCreditAnchorSuffix),
            "some_other_effect");

        var settled = await world.Reconciler().SettleConfirmedAsync(operation);

        settled.Should().BeFalse("a key space collision is unanswerable, so it goes to an operator");
        world.TreasuryCredits.Should().BeEmpty("nothing moved on an unanswerable question");
        operation.Parameters[ReserveExchangeManager.ProjectCreditAppliedParameter].Should().Be(
            "false", "the row keeps stating that the credit is outstanding");
    }

    [Fact]
    public async Task A_settlement_another_pass_still_holds_is_refused_rather_than_repeated()
    {
        // An anchor that is open and not ours means either a concurrent sweep is mid-credit or one
        // died mid-credit, and nothing can tell those apart. Under-crediting is visible and an
        // operator can finish it; crediting twice mints value out of nothing. So this refuses.
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedBuy(creditApplied: false, platformAmount: 700, projectUnits: 350);

        world.Anchors.SeedOpen(ExchangeSettlementReconciler.SettlementAnchor(
            LedgerKey, ExchangeSettlementReconciler.ReconcileCreditAnchorSuffix));

        var settled = await world.Reconciler().SettleConfirmedAsync(operation);

        settled.Should().BeFalse("nobody can say whether the held effect landed");
        world.TreasuryCredits.Should().BeEmpty("no counter moved on an unanswerable question");
        operation.Parameters[ReserveExchangeManager.ProjectCreditAppliedParameter].Should().Be(
            "false", "the row keeps stating that the credit is outstanding");
    }

    [Fact]
    public async Task A_credit_that_failed_once_is_retried_by_the_next_sweep_and_lands_once()
    {
        // The anchor must not turn one transient treasury blip into permanent manual work. The
        // ledger will not let a settled key be settled again — the package updates WHERE the row
        // is still InProgress — so a retry takes the NEXT generation of the key rather than
        // reusing a spent one. Reusing it would throw in production and pass against a permissive
        // fake, which is why this world's ledger enforces the same precondition.
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedBuy(creditApplied: false, platformAmount: 700, projectUnits: 350);

        world.TreasuryRefusesMovements();
        (await world.Reconciler().SettleConfirmedAsync(operation)).Should().BeFalse();
        world.TreasuryCredits.Should().BeEmpty("the treasury refused, so nothing moved");

        world.TreasuryAcceptsMovements();

        var second = await world.Reconciler().SettleConfirmedAsync(operation);

        second.Should().BeTrue("a transient refusal is what the next sweep is for");
        world.TreasuryCredits.Should().ContainSingle().Which.Should().Be(new BigInteger(700));
        world.Anchors.Settled.Should().BeTrue(
            "the retry generation was settled through the ledger's own InProgress precondition, "
            + "not by re-settling a key the ledger had already closed");

        // And a third sweep, with the window past this key, still moves nothing.
        world.TreasuryForgetsReplayKeys();
        operation.Parameters[ReserveExchangeManager.ProjectCreditAppliedParameter] = "false";
        (await world.Reconciler().SettleConfirmedAsync(operation)).Should().BeTrue();
        world.TreasuryCredits.Should().ContainSingle("the credit landed once and stays landed");
    }

    // ── An accepted-then-failed group is actually unwound ────────────────────

    [Fact]
    public async Task A_second_compensation_pass_does_not_hand_the_partition_back_twice()
    {
        // CompensateFailedAsync is re-entered whenever ANY step in it returns false, and the
        // unwind runs BEFORE the treasury refund. So a refund that fails re-runs the unwind next
        // sweep — and the reserve's replay window is bounded exactly as the treasury's is. Once it
        // has evicted the key, the partition is handed back a second time for a group that
        // happened once, and the reserve reports units nobody ever took.
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedSell(platformAmount: 700, projectUnits: 350, treasurySpent: true);

        world.Draw(ReserveDrawKind.Issue, 350);
        world.Draw(ReserveDrawKind.Return, 350);
        world.Partition().Available.Should().Be(new BigInteger(1000));

        world.TreasuryRefusesMovements();
        (await world.Reconciler().CompensateFailedAsync(operation)).Should().BeFalse(
            "the treasury refund could not be recorded, so the operator still has work to do");
        world.Partition().Available.Should().Be(
            new BigInteger(650), "the unwind itself landed before the refund failed");

        // The next sweep, with both bounded windows past this exchange's keys.
        world.ReserveForgetsReplayKeys();
        world.TreasuryForgetsReplayKeys();
        world.TreasuryAcceptsMovements();

        (await world.Reconciler().CompensateFailedAsync(operation)).Should().BeTrue(
            "the refund is now recordable and the unwind is already done");

        world.Partition().Available.Should().Be(
            new BigInteger(650),
            "the units were re-issued ONCE; a second unwind would draw the partition again for a "
            + "group that only ever happened once");
        world.TreasuryCredits.Should().ContainSingle().Which.Should().Be(
            new BigInteger(700), "and the refund lands exactly once");
    }

    [Fact]
    public async Task A_buy_that_failed_on_chain_gives_the_partition_back()
    {
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedBuy(
            creditApplied: false, platformAmount: 700, projectUnits: 350,
            commitmentKind: ReserveExchangeManager.CommitmentKinds.PartitionIssue);

        // The partition as the in-request Issue draw left it.
        world.Draw(ReserveDrawKind.Issue, 350);
        world.Partition().Available.Should().Be(new BigInteger(650));

        (await world.Reconciler().CompensateFailedAsync(operation)).Should().BeTrue();

        world.Partition().Available.Should().Be(
            new BigInteger(1000),
            "a group that died after acceptance left the earmark permanently spoken for; nothing "
            + "unwound it, and the next sponsor was refused for units nobody holds");
        world.TreasuryCredits.Should().BeEmpty("a buy spends no project treasury to put back");
    }

    [Fact]
    public async Task A_sell_that_failed_on_chain_puts_the_treasury_back_and_re_draws_the_partition()
    {
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedSell(
            platformAmount: 700, projectUnits: 350, treasurySpent: true);

        // The partition as the in-request Return draw left it: units came BACK on a sell.
        world.Draw(ReserveDrawKind.Issue, 350);
        world.Draw(ReserveDrawKind.Return, 350);
        world.Partition().Available.Should().Be(new BigInteger(1000));

        (await world.Reconciler().CompensateFailedAsync(operation)).Should().BeTrue();

        world.Partition().Available.Should().Be(
            new BigInteger(650), "the units the sell said came back never did");
        world.TreasuryCredits.Should().ContainSingle().Which.Should().Be(
            new BigInteger(700),
            "the project paid for units it never received; without this the treasury stays short");
    }

    [Fact]
    public async Task A_row_that_does_not_name_its_commitment_hands_nothing_back()
    {
        var world = new ExchangeWorld(declaredPartition: 1000);
        var operation = world.SeedBuy(
            creditApplied: false, platformAmount: 700, projectUnits: 350, commitmentKind: null);

        world.Draw(ReserveDrawKind.Issue, 350);

        (await world.Reconciler().CompensateFailedAsync(operation)).Should().BeFalse(
            "a guessed unwind hands the partition units nobody took — refusing and flagging is "
            + "the safe answer");

        world.Partition().Available.Should().Be(
            new BigInteger(650), "no counter moved on a guess");
    }

    // ── The world under test ─────────────────────────────────────────────────

    private sealed class ExchangeWorld
    {
        private readonly CountingReserve _reserve;
        private readonly Mock<IProjectTreasury> _treasury = new();
        private readonly Mock<IPlatformHold> _holds = new();
        private readonly Mock<IBlockchainOperationStore> _operations = new();

        private bool _treasuryRefuses;
        private bool _treasuryForgetsKeys;
        private bool _operationWritesFail;

        public ExchangeWorld(long declaredPartition)
        {
            _reserve = new CountingReserve(Chain, ProjectAsset, PartitionName, declaredPartition);

            _treasury
                .Setup(t => t.RecordMovementAsync(
                    It.IsAny<ProjectTreasuryMovementRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProjectTreasuryMovementRequest request, CancellationToken _) =>
                {
                    if (_treasuryRefuses)
                        return AZOAResult<ProjectTreasuryMovementOutcome>.Failure("The ledger is unavailable.");

                    // The real ledger is idempotent on the operation key ONLY inside a bounded
                    // window (ProjectTreasuryService.MovementReplayWindowSize = 128, evicted
                    // oldest-first). ForgetsReplayKeys models the key having fallen out of it,
                    // which is what 128 other movements on one project treasury do.
                    if (!AppliedKeys.Add(request.OperationKey) && !_treasuryForgetsKeys)
                    {
                        return AZOAResult<ProjectTreasuryMovementOutcome>.Success(
                            new ProjectTreasuryMovementOutcome(
                                request.AssetId, request.Kind, request.Amount,
                                new ProjectTreasuryAssetCounters(
                                    request.Amount, BigInteger.Zero, BigInteger.Zero),
                                1, Replayed: true));
                    }

                    TreasuryCredits.Add(request.Amount);
                    return AZOAResult<ProjectTreasuryMovementOutcome>.Success(
                        new ProjectTreasuryMovementOutcome(
                            request.AssetId, request.Kind, request.Amount,
                            new ProjectTreasuryAssetCounters(
                                request.Amount, BigInteger.Zero, BigInteger.Zero),
                            1, Replayed: false));
                });

            _operations
                .Setup(s => s.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IBlockchainOperation op, CancellationToken _) =>
                    _operationWritesFail
                        ? AZOAResult<IBlockchainOperation>.Failure("The operation store is unavailable.")
                        : AZOAResult<IBlockchainOperation>.Success(op));
        }

        public List<BigInteger> TreasuryCredits { get; } = new();

        /// <summary>The durable anchors the reconciler keeps for itself, shared across sweeps.</summary>
        public StrictAnchorLedger Anchors { get; } = new();

        private HashSet<string> AppliedKeys { get; } = new(StringComparer.Ordinal);

        public void TreasuryRefusesMovements() => _treasuryRefuses = true;

        public void TreasuryAcceptsMovements() => _treasuryRefuses = false;

        /// <summary>
        /// Models the bounded replay window having evicted this exchange's key: 128 movements on
        /// one project treasury and a repeat is no longer recognised as a repeat.
        /// </summary>
        public void TreasuryForgetsReplayKeys() => _treasuryForgetsKeys = true;

        /// <summary>The same eviction on the reserve's own window (DrawReplayWindowSize = 128).</summary>
        public void ReserveForgetsReplayKeys() => _reserve.ForgetReplayKeys();

        /// <summary>The write that leaves the row's flag stale while the money has already moved.</summary>
        public void OperationWritesFail() => _operationWritesFail = true;

        public void OperationWritesSucceed() => _operationWritesFail = false;

        /// <summary>A NEW reconciler each call, as the scoped registration gives each sweep.</summary>
        public ExchangeSettlementReconciler Reconciler() =>
            new(_treasury.Object, _reserve, _holds.Object, _operations.Object, Anchors);

        public ReservePartitionCounters Partition() => _reserve.Counters();

        /// <summary>Moves the partition the way the in-request path moved it.</summary>
        public void Draw(ReserveDrawKind kind, long amount) =>
            _reserve.DrawAsync(new ReserveDrawRequest(
                    Chain, ProjectAsset, PartitionName, kind, new BigInteger(amount),
                    $"{LedgerKey}:{kind}", ReserveDrawAuthority.ForTenant(TenantId)))
                .GetAwaiter().GetResult();

        public BlockchainOperation SeedBuy(
            bool creditApplied,
            long platformAmount,
            long projectUnits,
            string? commitmentKind = ReserveExchangeManager.CommitmentKinds.PartitionIssue) =>
            Seed(ExchangeDirection.Buy, platformAmount, projectUnits, commitmentKind,
                creditApplied: creditApplied, treasurySpent: false);

        public BlockchainOperation SeedSell(
            long platformAmount, long projectUnits, bool treasurySpent = false) =>
            Seed(ExchangeDirection.Sell, platformAmount, projectUnits,
                ReserveExchangeManager.CommitmentKinds.PartitionReturn,
                creditApplied: null, treasurySpent: treasurySpent);

        /// <summary>An Exchange row exactly as <c>ReserveExchangeManager.BuildOperation</c> writes it.</summary>
        private static BlockchainOperation Seed(
            ExchangeDirection direction,
            long platformAmount,
            long projectUnits,
            string? commitmentKind,
            bool? creditApplied,
            bool treasurySpent)
        {
            var buying = direction == ExchangeDirection.Buy;

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TxHash"] = "GROUPLEG1",
                ["ChainType"] = Chain,
                ["Amount"] = (buying ? platformAmount : projectUnits).ToString(),
                ["AmountOut"] = (buying ? projectUnits : platformAmount).ToString(),
                ["SourceTokenId"] = buying ? PlatformAsset : ProjectAsset,
                ["TargetTokenId"] = buying ? ProjectAsset : PlatformAsset,
                ["ExchangeDirection"] = direction.ToString(),
                ["ProjectReference"] = ProjectReference,
                ["ReserveAddress"] = ReserveAddress,
                ["ReservePartition"] = PartitionName,
                ["IdempotencyKey"] = LedgerKey,
                [IdempotencyParameterNames.ResultPayload] =
                    IdempotencyReplay.SerializeForReplay(new ExchangeReceipt
                    {
                        ReceiptReference = "correlation-1",
                        State = ExchangeReceiptState.Completed,
                    }),
                [ReserveExchangeManager.CommitmentTreasurySpentParameter] =
                    treasurySpent ? "true" : "false",
            };

            if (commitmentKind is not null)
                parameters[ReserveExchangeManager.CommitmentKindParameter] = commitmentKind;

            if (creditApplied is { } applied)
            {
                parameters[ReserveExchangeManager.ProjectCreditAppliedParameter] =
                    applied ? "true" : "false";
            }

            return new BlockchainOperation
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                ActingTenantId = TenantId,
                OperationType = ReserveExchangeManager.ChainOperationType,
                Status = OperationStatus.PendingConfirmation,
                Amount = RailAmount.OfChainAsset(new BigInteger(buying ? platformAmount : projectUnits)),
                CreatedDate = DateTime.UtcNow.AddMinutes(-30),
                Parameters = parameters,
            };
        }
    }

    /// <summary>
    /// An idempotency ledger with the PACKAGE's precondition, not a permissive stand-in.
    /// </summary>
    /// <remarks>
    /// SurrealForge's ledger settles a key with
    /// <c>UPDATE ... SET state = $_next WHERE state = $_expected</c> and throws "Cannot complete
    /// idempotency key" when the row has already moved on. A fake that quietly allowed
    /// Failed → Completed would let an anchor design pass here and throw in production the first
    /// time an effect was retried — the exact shape of defect this wave keeps finding, so this
    /// world refuses to be that fake.
    /// </remarks>
    private sealed class StrictAnchorLedger : IIdempotencyStore
    {
        private readonly Dictionary<string, IdempotencyRecord> _records = new(StringComparer.Ordinal);

        /// <summary>True once any key has been settled through the real precondition.</summary>
        public bool Settled { get; private set; }

        /// <summary>
        /// A row at <paramref name="key"/> that belongs to somebody else — the shape a caller
        /// produces when its own idempotency key lands in the settlement key space.
        /// </summary>
        public void SeedForeignCompleted(string key, string operationType) =>
            _records[key] = new IdempotencyRecord
            {
                Key = key,
                OperationType = operationType,
                State = IdempotencyState.Completed,
            };

        /// <summary>An anchor another pass claimed and never settled.</summary>
        public void SeedOpen(string key) =>
            _records[key] = new IdempotencyRecord
            {
                Key = key,
                OperationType = "reserve_exchange_settlement",
                State = IdempotencyState.InProgress,
            };

        public Task<IdempotencyClaim> TryClaimAsync(string key, string operationType, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var existing))
                return Task.FromResult(new IdempotencyClaim(false, existing));

            var mine = new IdempotencyRecord
            {
                Key = key,
                OperationType = operationType,
                State = IdempotencyState.InProgress,
            };
            _records[key] = mine;
            return Task.FromResult(new IdempotencyClaim(true, mine));
        }

        public Task CompleteAsync(string key, string resultPayload, CancellationToken ct)
            => Settle(key, IdempotencyState.Completed, "complete");

        public Task FailAsync(string key, string error, CancellationToken ct)
            => Settle(key, IdempotencyState.Failed, "fail");

        public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
            => Task.FromResult(_records.TryGetValue(key, out var found) ? found : null);

        private Task Settle(string key, IdempotencyState next, string verb)
        {
            if (!_records.TryGetValue(key, out var record) || record.State != IdempotencyState.InProgress)
            {
                throw new InvalidOperationException(
                    $"Cannot {verb} idempotency key '{key}': it is not InProgress. The package's "
                    + "UPDATE carries WHERE state = InProgress, so this is what production does.");
            }

            record.State = next;
            Settled = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A reserve backed by the REAL partition arithmetic and the real replay window, so an
    /// oversubscribed or duplicated draw is refused here for the reason it is refused in
    /// production.
    /// </summary>
    private sealed class CountingReserve : IPlatformReserve
    {
        private readonly string _chain;
        private readonly string _assetId;
        private readonly string _partition;
        private readonly HashSet<string> _appliedKeys = new(StringComparer.Ordinal);
        private ReservePartitionCounters _counters;

        public CountingReserve(string chain, string assetId, string partition, long declared)
        {
            _chain = chain;
            _assetId = assetId;
            _partition = ReservePartitionCodec.Normalize(partition);
            _counters = ReservePartitionCounters.Declaring(new BigInteger(declared));
        }

        public ReservePartitionCounters Counters() => _counters;

        private bool _forgetsKeys;

        /// <summary>Models PlatformReserveService's 128-key window having evicted this key.</summary>
        public void ForgetReplayKeys() => _forgetsKeys = true;

        public Task<AZOAResult<ReserveDrawOutcome>> DrawAsync(
            ReserveDrawRequest request, CancellationToken ct = default)
        {
            if (request.Authority is null)
            {
                return Task.FromResult(AZOAResult<ReserveDrawOutcome>.Failure(
                    "A draw arrived with no stated authority."));
            }

            var partition = ReservePartitionCodec.Normalize(request.Partition);
            if (partition != _partition)
            {
                return Task.FromResult(AZOAResult<ReserveDrawOutcome>.Failure(
                    $"Partition '{partition}' is not declared on asset '{_assetId}'."));
            }

            if (!_appliedKeys.Add(request.OperationKey) && !_forgetsKeys)
            {
                return Task.FromResult(AZOAResult<ReserveDrawOutcome>.Success(
                    new ReserveDrawOutcome(
                        _assetId, partition, request.Kind, request.Amount, _counters, 1, Replayed: true)));
            }

            if (!_counters.TryApply(request.Kind, request.Amount, partition, out var next, out var error))
                return Task.FromResult(AZOAResult<ReserveDrawOutcome>.Failure(error));

            _counters = next;
            return Task.FromResult(AZOAResult<ReserveDrawOutcome>.Success(
                new ReserveDrawOutcome(
                    _assetId, partition, request.Kind, request.Amount, next, 1, Replayed: false)));
        }

        public Task<AZOAResult<ReserveAccount>> GetAccountAsync(
            string chainType, CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<ReserveAccount>.Success(
                new ReserveAccount(_chain, ReserveAddress, IsFeePayer: true)));

        public Task<AZOAResult<PlatformReserveLedger>> GetLedgerAsync(
            string chainType, string assetId, CancellationToken ct = default) =>
            throw new NotSupportedException("The settlement reconciler reads no ledger row.");

        public Task<AZOAResult<string>> GetBalanceAsync(
            string chainType, string? assetId = null, CancellationToken ct = default) =>
            throw new NotSupportedException("The settlement reconciler reads no chain balance.");

        public Task<AZOAResult<T>> SignAsReserveAsync<T>(
            string chainType, SigningContext signingContext, Func<byte[], Task<T>> sign) =>
            throw new NotSupportedException("The settlement reconciler signs nothing.");

        public Task<AZOAResult<PlatformReserveLedger>> RegisterAssetAsync(
            ReserveAssetRegistration registration, CancellationToken ct = default) =>
            throw new NotSupportedException("The settlement reconciler registers no assets.");

        public Task<AZOAResult<PlatformReserveLedger>> DeclarePartitionsAsync(
            string chainType,
            string assetId,
            IReadOnlyDictionary<string, BigInteger> declaredByPartition,
            ReserveDrawAuthority authority,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Partitions are declared by the operator.");
    }
}
