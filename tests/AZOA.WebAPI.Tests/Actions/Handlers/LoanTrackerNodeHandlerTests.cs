using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Services.Lending;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions.Handlers;

/// <summary>
/// Dispatch tests for the D21 principal-only loan tracker: <c>LoanOpen</c>,
/// <c>LoanRepaymentRecord</c>, <c>LoanBalance</c>.
///
/// <para>The load-bearing behaviours: all three nodes are Tier-1 (they record, they never
/// move value); a partial repayment and a write-off are ordinary Ok results, not failures;
/// over-repayment is the one genuine error; every amount round-trips as a base-unit decimal
/// string; a ledger row that cannot say how much it moved fails loudly instead of reading
/// as zero; and reads are runner-scoped so another user's tracker is indistinguishable from
/// one that does not exist.</para>
/// </summary>
public class LoanTrackerNodeHandlerTests
{
    private const string Algo = "ALGO";

    // ── the three nodes are Tier-1 ──────────────────────────────────────────

    [Fact]
    public void AllThreeNodes_DeclareTheirType_AndAreChainFree()
    {
        var store = new InMemoryHolonManager();

        IActionNodeHandler open = new LoanOpenNodeHandler(store);
        IActionNodeHandler record = new LoanRepaymentRecordNodeHandler(store);
        IActionNodeHandler balance = new LoanBalanceNodeHandler(store);

        open.NodeType.Should().Be(ActionNodeType.LoanOpen);
        record.NodeType.Should().Be(ActionNodeType.LoanRepaymentRecord);
        balance.NodeType.Should().Be(ActionNodeType.LoanBalance);

        open.RequiresChainCapability.Should().BeFalse("a tracker records value moves, it never makes them");
        record.RequiresChainCapability.Should().BeFalse();
        balance.RequiresChainCapability.Should().BeFalse();
    }

    // ── LoanOpen ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoanOpen_CreatesTracker_AndItsDisbursementEntry()
    {
        var store = new InMemoryHolonManager();
        var runner = Guid.NewGuid();

        var result = await new LoanOpenNodeHandler(store).HandleAsync(
            Ctx(ActionNodeType.LoanOpen, new LoanOpenNodeConfig
            {
                Principal = "1000",
                CurrencyCode = Algo,
                Name = "sept-loan",
                Reference = "disbursement-tx-1",
            }, runner));

        result.IsError.Should().BeFalse(result.Message);

        var projection = Read(result.Output);
        projection.Principal.Should().Be("1000");
        projection.TotalRepaid.Should().Be("0");
        projection.TotalWrittenOff.Should().Be("0");
        projection.Outstanding.Should().Be("1000");
        projection.Status.Should().Be(nameof(LoanStatus.Open));
        projection.EntryCount.Should().Be(1);

        var tracker = store.All().Single(h => h.AssetType == LoanHolonTypes.LoanTracker);
        tracker.UserId.Should().Be(runner, "the tracker belongs to the RUNNER, not the action author");
        tracker.Metadata[LoanHolonTypes.MetadataKeys.Principal].Should().Be("1000");
        tracker.Metadata[LoanHolonTypes.MetadataKeys.CurrencyCode].Should().Be(Algo);
        tracker.Metadata[LoanHolonTypes.MetadataKeys.Status].Should().Be(nameof(LoanStatus.Open));

        var entry = store.All().Single(h => h.AssetType == LoanHolonTypes.LoanLedgerEntry);
        entry.ParentHolonId.Should().Be(tracker.Id);
        entry.Metadata[LoanHolonTypes.MetadataKeys.EntryKind].Should().Be(nameof(LoanLedgerEntryKind.Disbursement));
        entry.Metadata[LoanHolonTypes.MetadataKeys.Amount].Should().Be("1000");
    }

    [Fact]
    public async Task LoanOpen_PersistsHugeAmounts_AsDecimalStrings()
    {
        // 1e21 base units — past both ulong.MaxValue (D13) and 2^53, so a numeric
        // column or a JSON number would have mangled it.
        const string wei = "1000000000000000000000";
        var store = new InMemoryHolonManager();

        var result = await new LoanOpenNodeHandler(store).HandleAsync(
            Ctx(ActionNodeType.LoanOpen, new LoanOpenNodeConfig { Principal = wei, CurrencyCode = "ETH" }, Guid.NewGuid()));

        result.IsError.Should().BeFalse(result.Message);
        Read(result.Output).Outstanding.Should().Be(wei);
        result.Output.Should().Contain($"\"{wei}\"", "amounts are transported as strings, never JSON numbers");
    }

    [Theory]
    [InlineData("", Algo)]              // no principal
    [InlineData("not-a-number", Algo)]  // unparseable
    [InlineData("-5", Algo)]            // signed — RailAmount.TryParse is NumberStyles.None
    [InlineData("1_000", Algo)]         // separators
    [InlineData("0", Algo)]             // tracks nothing
    [InlineData("1000", "")]            // unit not named
    public async Task LoanOpen_RejectsUnusableConfig_Terminally(string principal, string currencyCode)
    {
        var store = new InMemoryHolonManager();

        var result = await new LoanOpenNodeHandler(store).HandleAsync(
            Ctx(ActionNodeType.LoanOpen,
                new LoanOpenNodeConfig { Principal = principal, CurrencyCode = currencyCode },
                Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Retriable.Should().BeFalse("a config that can never succeed must not burn a retry budget");
        store.All().Should().BeEmpty("nothing is written when the config is refused");
    }

    // ── LoanRepaymentRecord: partial, full, write-off ────────────────────────

    [Fact]
    public async Task PartialRepayment_ReturnsOk_AndLeavesTheLoanOpen()
    {
        var (store, runner, loanId) = await OpenLoanAsync("1000");

        var result = await RecordAsync(store, runner, loanId, "400");

        result.IsError.Should().BeFalse(result.Message);
        var projection = Read(result.Output);
        projection.TotalRepaid.Should().Be("400");
        projection.Outstanding.Should().Be("600");
        projection.Status.Should().Be(nameof(LoanStatus.Open), "a partial repayment is an ordinary state, not an error");
    }

    [Fact]
    public async Task RepaymentsAccumulate_AndTheFinalOneClosesTheLoan()
    {
        var (store, runner, loanId) = await OpenLoanAsync("1000");

        await RecordAsync(store, runner, loanId, "400", reference: "tx-a");
        var second = await RecordAsync(store, runner, loanId, "600", reference: "tx-b");

        second.IsError.Should().BeFalse(second.Message);
        var projection = Read(second.Output);
        projection.TotalRepaid.Should().Be("1000");
        projection.Outstanding.Should().Be("0");
        projection.Status.Should().Be(nameof(LoanStatus.Repaid));
        projection.EntryCount.Should().Be(3, "one disbursement plus two repayments");

        var tracker = store.All().Single(h => h.Id == loanId);
        tracker.Metadata[LoanHolonTypes.MetadataKeys.Status]
            .Should().Be(nameof(LoanStatus.Repaid), "the derived status is mirrored for gate predicates");
    }

    [Fact]
    public async Task PartialWriteOff_LeavesTheLoanOpen_BecauseTheRestIsStillCollectible()
    {
        // The status is derived from the BALANCE, not from the presence of a write-off row.
        // Reading a 100-unit write-off against 1000 as terminally WrittenOff would declare
        // 900 of still-collectible principal abandoned.
        var (store, runner, loanId) = await OpenLoanAsync("1000");

        var result = await RecordAsync(store, runner, loanId, "100", nameof(LoanLedgerEntryKind.WriteOff), "wo-1");

        result.IsError.Should().BeFalse(result.Message);
        var projection = Read(result.Output);
        projection.TotalWrittenOff.Should().Be("100");
        projection.Outstanding.Should().Be("900");
        projection.Status.Should().Be(nameof(LoanStatus.Open), "900 base units are still collectible");

        var tracker = store.All().Single(h => h.Id == loanId);
        tracker.Metadata[LoanHolonTypes.MetadataKeys.Status].Should().Be(nameof(LoanStatus.Open));
    }

    [Fact]
    public async Task AfterAPartialWriteOff_TheLoanStillAcceptsTheRemainingRepayment()
    {
        // The proof that a partial write-off is not terminal: the tracker keeps taking rows.
        var (store, runner, loanId) = await OpenLoanAsync("1000");
        await RecordAsync(store, runner, loanId, "100", nameof(LoanLedgerEntryKind.WriteOff), "wo-1");

        var result = await RecordAsync(store, runner, loanId, "900", reference: "tx-rest");

        result.IsError.Should().BeFalse(result.Message);
        var projection = Read(result.Output);
        projection.TotalRepaid.Should().Be("900");
        projection.TotalWrittenOff.Should().Be("100");
        projection.Outstanding.Should().Be("0");
        projection.Status.Should().Be(nameof(LoanStatus.WrittenOff), "part of the principal was abandoned");
    }

    [Fact]
    public async Task WriteOff_ReturnsOk_AndMovesTheLoanToWrittenOff()
    {
        var (store, runner, loanId) = await OpenLoanAsync("1000");
        await RecordAsync(store, runner, loanId, "250", reference: "tx-a");

        var result = await RecordAsync(store, runner, loanId, "750", nameof(LoanLedgerEntryKind.WriteOff), "tx-b");

        result.IsError.Should().BeFalse(result.Message);
        var projection = Read(result.Output);
        projection.TotalRepaid.Should().Be("250");
        projection.TotalWrittenOff.Should().Be("750");
        projection.Outstanding.Should().Be("0");
        projection.Status.Should().Be(nameof(LoanStatus.WrittenOff), "a write-off ends the tracker, it does not fail it");
    }

    [Fact]
    public async Task OverRepayment_Fails_BecauseTheLedgerWouldDisagreeWithReality()
    {
        var (store, runner, loanId) = await OpenLoanAsync("1000");

        var result = await RecordAsync(store, runner, loanId, "1001");

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("exceeds the outstanding balance");
        store.All().Count(h => h.AssetType == LoanHolonTypes.LoanLedgerEntry)
            .Should().Be(1, "the refused row is not appended");
    }

    [Fact]
    public async Task DisbursementKind_IsRefused_ThisNodeNeverOpensALoan()
    {
        var (store, runner, loanId) = await OpenLoanAsync("1000");

        var result = await RecordAsync(store, runner, loanId, "10", nameof(LoanLedgerEntryKind.Disbursement));

        result.IsError.Should().BeTrue();
        result.Retriable.Should().BeFalse();
        result.Message.Should().Contain("LoanOpen");
    }

    [Fact]
    public async Task SameReference_RecordsOnce_SoARetryCannotDoubleCount()
    {
        var (store, runner, loanId) = await OpenLoanAsync("1000");

        var first = await RecordAsync(store, runner, loanId, "400", reference: "transfer-tx-9");
        var replay = await RecordAsync(store, runner, loanId, "400", reference: "transfer-tx-9");

        first.IsError.Should().BeFalse(first.Message);
        replay.IsError.Should().BeFalse(replay.Message);
        replay.Message.Should().Contain("already recorded");
        Read(replay.Output).TotalRepaid.Should().Be("400", "the replay must not append a second repayment");
        store.All().Count(h => h.AssetType == LoanHolonTypes.LoanLedgerEntry).Should().Be(2);
    }

    [Fact]
    public async Task ARepaymentCitingTheDisbursementsReference_IsRecorded_NotSwallowedAsAReplay()
    {
        // LoanOpen stamps the DISBURSEMENT row with the tenant's own reference ("opening-tx").
        // Matching the replay guard against that row reported Ok — "already recorded" — for a
        // repayment that was never appended, so a real repayment vanished silently.
        var (store, runner, loanId) = await OpenLoanAsync("1000");

        var result = await RecordAsync(store, runner, loanId, "400", reference: "opening-tx");

        result.IsError.Should().BeFalse(result.Message);
        Read(result.Output).TotalRepaid.Should().Be("400", "the disbursement is not a prior repayment");
        store.All().Count(h => h.AssetType == LoanHolonTypes.LoanLedgerEntry)
            .Should().Be(2, "the repayment really was appended");
    }

    [Fact]
    public async Task ARetryOfTheSameExecution_RecordsOnce_EvenWithNoReference()
    {
        // The durable engine retries by design, and `reference` is OPTIONAL — so replay
        // protection cannot depend on it. Re-running the SAME (run, node) is what a retry is.
        var (store, runner, loanId) = await OpenLoanAsync("1000");
        var context = Ctx(
            ActionNodeType.LoanRepaymentRecord,
            new LoanRepaymentRecordNodeConfig { LoanHolonId = loanId, Amount = "400" },
            runner);

        var first = await new LoanRepaymentRecordNodeHandler(store).HandleAsync(context);
        var retry = await new LoanRepaymentRecordNodeHandler(store).HandleAsync(context);

        first.IsError.Should().BeFalse(first.Message);
        retry.IsError.Should().BeFalse(retry.Message);
        retry.Message.Should().Contain("already recorded");
        Read(retry.Output).TotalRepaid.Should().Be("400", "an engine retry must not double-count a repayment");
        store.All().Count(h => h.AssetType == LoanHolonTypes.LoanLedgerEntry).Should().Be(2);
    }

    [Fact]
    public async Task EveryRecordedRow_CarriesTheExecutionThatWroteIt()
    {
        var (store, runner, loanId) = await OpenLoanAsync("1000");
        var context = Ctx(
            ActionNodeType.LoanRepaymentRecord,
            new LoanRepaymentRecordNodeConfig { LoanHolonId = loanId, Amount = "400" },
            runner);

        await new LoanRepaymentRecordNodeHandler(store).HandleAsync(context);

        var repayment = store.All().Single(h =>
            h.AssetType == LoanHolonTypes.LoanLedgerEntry
            && h.Metadata[LoanHolonTypes.MetadataKeys.EntryKind] == nameof(LoanLedgerEntryKind.Repayment));

        repayment.Metadata[LoanHolonTypes.MetadataKeys.ExecutionKey]
            .Should().Be($"{context.RunId}:{context.NodeId}", "the stamp IS the unconditional replay key");
    }

    [Fact]
    public async Task TwoBranchesRecordingAtOnce_AreSerialized_SoBothCannotPassTheOverRepaymentGuard()
    {
        // Read-then-write: two branches each read 1000 outstanding, each pass the guard with
        // 600, and both append — 1200 against a 1000 principal. Holding the ledger gate across
        // read → guard → append is what makes the second branch see the first branch's row.
        var (store, runner, loanId) = await OpenLoanAsync("1000");

        var firstIsReading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        store.BeforeLedgerRead = async () =>
        {
            if (Interlocked.Increment(ref reads) != 1)
                return;

            firstIsReading.SetResult();
            await releaseFirstRead.Task;
        };

        var first = RecordAsync(store, runner, loanId, "600", reference: "branch-a");
        await firstIsReading.Task;

        var second = RecordAsync(store, runner, loanId, "600", reference: "branch-b");
        await Task.WhenAny(second, Task.Delay(250));

        second.IsCompleted.Should().BeFalse(
            "the second branch must not read a balance the first branch is about to change");

        releaseFirstRead.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Count(r => r.IsError).Should().Be(1, "1200 never moved, so exactly one 600 row is refused");
        results.Single(r => r.IsError).Message.Should().Contain("exceeds the outstanding balance");
        store.All().Count(h => h.AssetType == LoanHolonTypes.LoanLedgerEntry)
            .Should().Be(2, "one disbursement plus the single accepted repayment");
        Read(results.Single(r => !r.IsError).Output).TotalRepaid.Should().Be("600");
    }

    // ── scoping and loud failure ────────────────────────────────────────────

    [Fact]
    public async Task AnotherUsersLoan_IsIndistinguishableFromOneThatDoesNotExist()
    {
        var (store, _, loanId) = await OpenLoanAsync("1000");
        var stranger = Guid.NewGuid();

        var foreignRead = await BalanceAsync(store, stranger, loanId);
        var missingRead = await BalanceAsync(store, stranger, Guid.NewGuid());

        foreignRead.IsError.Should().BeTrue();
        foreignRead.Message.Should().Be($"[LoanBalance] {LoanLedgerReader.NotFound(loanId)}");
        missingRead.Message.Should().NotBe(foreignRead.Message);
        foreignRead.Message.Should().NotContain("owned by",
            "the two answers differ only in the id, so existence cannot be probed across users");
    }

    [Fact]
    public async Task PublicTrackerOwnedByAnotherUser_IsStillRefused()
    {
        var (store, _, loanId) = await OpenLoanAsync("1000");
        ((Holon)store.All().Single(h => h.Id == loanId)).IsPublic = true;

        var result = await BalanceAsync(store, Guid.NewGuid(), loanId);

        result.IsError.Should().BeTrue("this lane is owner-strict: a public flag never widens a ledger read");
    }

    [Fact]
    public async Task UnparseableAmountOnALedgerRow_FailsLoudly_ItNeverReadsAsZero()
    {
        var (store, runner, loanId) = await OpenLoanAsync("1000");
        var entry = (Holon)store.All().Single(h => h.AssetType == LoanHolonTypes.LoanLedgerEntry);
        entry.Metadata[LoanHolonTypes.MetadataKeys.Amount] = "one thousand";

        var result = await BalanceAsync(store, runner, loanId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not a base-unit decimal string");
    }

    [Fact]
    public async Task MixedUnitLedger_FailsLoudly()
    {
        var (store, runner, loanId) = await OpenLoanAsync("1000");
        var entry = (Holon)store.All().Single(h => h.AssetType == LoanHolonTypes.LoanLedgerEntry);
        entry.Metadata[LoanHolonTypes.MetadataKeys.CurrencyCode] = "USDC";

        var result = await BalanceAsync(store, runner, loanId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("mixed-unit ledger cannot be summed");
    }

    [Fact]
    public async Task LoanBalance_Projects_WithoutWritingAnything()
    {
        var (store, runner, loanId) = await OpenLoanAsync("1000");
        await RecordAsync(store, runner, loanId, "250");
        var before = store.All().Count;
        store.ResetWriteCounters();

        var result = await BalanceAsync(store, runner, loanId);

        result.IsError.Should().BeFalse(result.Message);
        Read(result.Output).Outstanding.Should().Be("750");
        store.All().Count.Should().Be(before);
        store.Writes.Should().Be(0, "LoanBalance is a pure read");
    }

    // ── the projection itself ───────────────────────────────────────────────

    [Fact]
    public void Projection_FloorsOutstandingAtZero_ButReportsTheExcessRatherThanHidingIt()
    {
        var loan = Tracker(Guid.NewGuid(), "1000", Algo);
        var entries = new[]
        {
            Entry(loan.Id, loan.UserId!.Value, LoanLedgerEntryKind.Repayment, "900"),
            Entry(loan.Id, loan.UserId!.Value, LoanLedgerEntryKind.Repayment, "900"),
        };

        var totals = LoanBalanceProjection.Compute(loan, entries);

        totals.Outstanding.ToBaseUnitString().Should().Be("0", "no row may express a negative debt");
        totals.TotalRepaid.ToBaseUnitString().Should().Be("1800");
        totals.Excess.ToBaseUnitString().Should().Be("800", "the floor removed 800 and says so");
        totals.Status.Should().Be(
            LoanStatus.OverRepaid,
            "a ledger returning more than the principal is an inconsistency, not a clean Repaid");
    }

    [Fact]
    public void Projection_PartialWriteOff_KeepsTheLoanOpen()
    {
        var loan = Tracker(Guid.NewGuid(), "1000", Algo);
        var entries = new[] { Entry(loan.Id, loan.UserId!.Value, LoanLedgerEntryKind.WriteOff, "100") };

        var totals = LoanBalanceProjection.Compute(loan, entries);

        totals.Outstanding.ToBaseUnitString().Should().Be("900");
        totals.TotalWrittenOff.ToBaseUnitString().Should().Be("100");
        totals.Status.Should().Be(
            LoanStatus.Open,
            "status follows the outstanding balance, not the presence of a write-off row");
    }

    [Fact]
    public void Projection_WriteOffThatClosesTheBalance_IsWrittenOffNotRepaid()
    {
        var loan = Tracker(Guid.NewGuid(), "1000", Algo);
        var entries = new[]
        {
            Entry(loan.Id, loan.UserId!.Value, LoanLedgerEntryKind.Repayment, "600"),
            Entry(loan.Id, loan.UserId!.Value, LoanLedgerEntryKind.WriteOff, "400"),
        };

        var totals = LoanBalanceProjection.Compute(loan, entries);

        totals.Outstanding.ToBaseUnitString().Should().Be("0");
        totals.Status.Should().Be(LoanStatus.WrittenOff, "part of the principal was abandoned to reach zero");
    }

    [Fact]
    public void Projection_TreatsDisbursementRowsAsFactsNotAdditions()
    {
        var loan = Tracker(Guid.NewGuid(), "1000", Algo);
        var entries = new[]
        {
            Entry(loan.Id, loan.UserId!.Value, LoanLedgerEntryKind.Disbursement, "1000"),
            Entry(loan.Id, loan.UserId!.Value, LoanLedgerEntryKind.Repayment, "1000"),
        };

        var totals = LoanBalanceProjection.Compute(loan, entries);

        totals.Principal.ToBaseUnitString().Should().Be("1000", "the tracker's principal is authoritative");
        totals.Outstanding.ToBaseUnitString().Should().Be("0");
        totals.EntryCount.Should().Be(2);
    }

    [Fact]
    public void Projection_RefusesAnEntryWithNoAmount()
    {
        var loan = Tracker(Guid.NewGuid(), "1000", Algo);
        var entry = Entry(loan.Id, loan.UserId!.Value, LoanLedgerEntryKind.Repayment, "10");
        entry.Metadata.Remove(LoanHolonTypes.MetadataKeys.Amount);

        var act = () => LoanBalanceProjection.Compute(loan, new[] { entry });

        act.Should().Throw<LoanLedgerFormatException>()
            .WithMessage("*carries no 'amount' value*");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static ActionNodeExecutionContext Ctx(ActionNodeType nodeType, object config, Guid actingUserId)
    {
        var node = new ActionNode
        {
            Id = Guid.NewGuid(),
            Name = "loan",
            NodeType = nodeType,
            Config = JsonSerializer.Serialize(config),
        };
        var action = new ActionEntity { Id = Guid.NewGuid(), Nodes = new List<ActionNode> { node } };
        return new ActionNodeExecutionContext(Guid.NewGuid(), node.Id, action, actingUserId);
    }

    private static LoanBalanceProjectionResult Read(string? outputJson)
    {
        outputJson.Should().NotBeNullOrWhiteSpace();
        return JsonSerializer.Deserialize<LoanBalanceProjectionResult>(outputJson!)!;
    }

    private static async Task<(InMemoryHolonManager Store, Guid Runner, Guid LoanId)> OpenLoanAsync(string principal)
    {
        var store = new InMemoryHolonManager();
        var runner = Guid.NewGuid();

        var result = await new LoanOpenNodeHandler(store).HandleAsync(
            Ctx(ActionNodeType.LoanOpen,
                new LoanOpenNodeConfig { Principal = principal, CurrencyCode = Algo, Reference = "opening-tx" },
                runner));
        result.IsError.Should().BeFalse(result.Message);

        return (store, runner, Guid.Parse(Read(result.Output).LoanId));
    }

    private static Task<ActionNodeHandlerResult> RecordAsync(
        InMemoryHolonManager store, Guid runner, Guid loanId, string amount,
        string? entryKind = null, string? reference = null) =>
        new LoanRepaymentRecordNodeHandler(store).HandleAsync(
            Ctx(ActionNodeType.LoanRepaymentRecord,
                new LoanRepaymentRecordNodeConfig
                {
                    LoanHolonId = loanId,
                    Amount = amount,
                    EntryKind = entryKind,
                    Reference = reference,
                },
                runner));

    private static Task<ActionNodeHandlerResult> BalanceAsync(InMemoryHolonManager store, Guid runner, Guid loanId) =>
        new LoanBalanceNodeHandler(store).HandleAsync(
            Ctx(ActionNodeType.LoanBalance, new LoanBalanceNodeConfig { LoanHolonId = loanId }, runner));

    private static Holon Tracker(Guid owner, string principal, string currencyCode) => new()
    {
        Id = Guid.NewGuid(),
        UserId = owner,
        AssetType = LoanHolonTypes.LoanTracker,
        Metadata = new Dictionary<string, string>
        {
            [LoanHolonTypes.MetadataKeys.Principal] = principal,
            [LoanHolonTypes.MetadataKeys.CurrencyKind] = nameof(AZOA.WebAPI.Core.ValueDimensionKind.ChainAsset),
            [LoanHolonTypes.MetadataKeys.CurrencyCode] = currencyCode,
            [LoanHolonTypes.MetadataKeys.Status] = nameof(LoanStatus.Open),
        },
    };

    private static Holon Entry(Guid loanId, Guid owner, LoanLedgerEntryKind kind, string amount) => new()
    {
        Id = Guid.NewGuid(),
        UserId = owner,
        ParentHolonId = loanId,
        AssetType = LoanHolonTypes.LoanLedgerEntry,
        Metadata = new Dictionary<string, string>
        {
            [LoanHolonTypes.MetadataKeys.EntryKind] = kind.ToString(),
            [LoanHolonTypes.MetadataKeys.LoanHolonId] = loanId.ToString(),
            [LoanHolonTypes.MetadataKeys.Amount] = amount,
            [LoanHolonTypes.MetadataKeys.CurrencyKind] = nameof(AZOA.WebAPI.Core.ValueDimensionKind.ChainAsset),
            [LoanHolonTypes.MetadataKeys.CurrencyCode] = Algo,
        },
    };

    /// <summary>
    /// The holon surface the lane actually uses — create, owner-or-public get, children,
    /// metadata-merging update — with the same owner-or-public read policy as
    /// <c>HolonManager</c>. Every other member is out of the lane's reach and throws.
    /// </summary>
    private sealed class InMemoryHolonManager : IHolonManager
    {
        private readonly Dictionary<Guid, Holon> _holons = new();
        private int _sequence;

        /// <summary>Count of mutating calls, so a read-only node can be proven read-only.</summary>
        public int Writes { get; private set; }

        /// <summary>
        /// Awaited at the start of every ledger read. Lets a test park one branch INSIDE its
        /// read, which is the only way to prove the read-check-append sequence is serialized
        /// rather than merely fast.
        /// </summary>
        public Func<Task>? BeforeLedgerRead { get; set; }

        public IReadOnlyList<IHolon> All() => _holons.Values.ToList();

        public void ResetWriteCounters() => Writes = 0;

        public Task<AZOAResult<IHolon>> GetAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null) =>
            Task.FromResult(_holons.TryGetValue(id, out var holon) && (holon.UserId == callerUserId || holon.IsPublic)
                ? new AZOAResult<IHolon> { Result = holon }
                : new AZOAResult<IHolon> { IsError = true, Message = $"Holon {id} not found." });

        public Task<AZOAResult<IHolon>> CreateAsync(HolonCreateModel model, Guid userId, AZOARequest? request = null)
        {
            Writes++;
            var holon = new Holon
            {
                Id = Guid.NewGuid(),
                Name = model.Name,
                Description = model.Description,
                ParentHolonId = model.ParentHolonId,
                UserId = userId,
                AssetType = model.AssetType,
                Metadata = new Dictionary<string, string>(model.Metadata),
                CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(_sequence++),
            };
            _holons[holon.Id] = holon;
            return Task.FromResult(new AZOAResult<IHolon> { Result = holon });
        }

        public Task<AZOAResult<IHolon>> UpdateAsync(Guid id, HolonUpdateModel model, Guid? userId = null, AZOARequest? request = null)
        {
            Writes++;
            if (!_holons.TryGetValue(id, out var holon) || (userId.HasValue && holon.UserId != userId))
                return Task.FromResult(new AZOAResult<IHolon> { IsError = true, Message = $"Holon {id} not found." });

            if (model.Metadata is not null)
                foreach (var (key, value) in model.Metadata)
                    holon.Metadata[key] = value;

            return Task.FromResult(new AZOAResult<IHolon> { Result = holon });
        }

        public async Task<AZOAResult<IEnumerable<IHolon>>> GetChildrenAsync(Guid parentId, Guid? callerUserId = null, AZOARequest? request = null)
        {
            if (BeforeLedgerRead is { } hook)
                await hook();

            return new AZOAResult<IEnumerable<IHolon>>
            {
                Result = _holons.Values
                    .Where(h => h.ParentHolonId == parentId && (h.UserId == callerUserId || h.IsPublic))
                    .Cast<IHolon>()
                    .ToList(),
            };
        }

        public Task<AZOAResult<IEnumerable<IHolon>>> GetAllAsync(Guid? callerUserId = null, AZOARequest? request = null) =>
            throw new NotSupportedException();

        public Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid? userId = null, AZOARequest? request = null) =>
            throw new NotSupportedException();

        public Task<AZOAResult<IEnumerable<IHolon>>> QueryAsync(HolonQueryRequest query, Guid? callerUserId = null, AZOARequest? request = null) =>
            throw new NotSupportedException();

        public Task<AZOAResult<IHolon>> InteractAsync(Guid id, HolonInteractionRequest request, Guid? userId = null, AZOARequest? providerRequest = null) =>
            throw new NotSupportedException();

        public Task<AZOAResult<IEnumerable<IHolon>>> GetPeersAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null) =>
            throw new NotSupportedException();

        public Task<AZOAResult<IEnumerable<IHolon>>> GetAncestorsAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null) =>
            throw new NotSupportedException();

        public Task<AZOAResult<IEnumerable<IHolon>>> GetDescendantsAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null) =>
            throw new NotSupportedException();

        public Task<AZOAResult<int>> PropagateAsync(Guid id, HolonPropagateRequest request, Guid? userId = null, AZOARequest? providerRequest = null) =>
            throw new NotSupportedException();

        public Task<AZOAResult<HolonComposition>> ComposeAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null) =>
            throw new NotSupportedException();

        public Task<AZOAResult<IHolon>> CloneAsync(Guid id, HolonCloneRequest request, Guid userId, AZOARequest? providerRequest = null) =>
            throw new NotSupportedException();

        public Task<AZOAResult<bool>> MoveSubtreeAsync(Guid id, Guid newParentId, Guid? userId = null, AZOARequest? request = null) =>
            throw new NotSupportedException();
    }
}
