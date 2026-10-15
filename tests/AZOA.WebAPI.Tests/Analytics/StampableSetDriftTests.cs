// SPDX-License-Identifier: UNLICENSED
//
// §value-stamp drift. Twice now the READ side of "which operations move one identifiable
// quantity" has parted company with the WRITE side, and both times the symptom was a row that
// answered "pricing": "Priced" beside "isStampable": false — a contradiction on a financial
// surface. The second time it was IssueUnits: the write side learned it in the same commit that
// left the read side behind, so units credited into a worker's or a sponsor's account contributed
// ZERO to their own position totals and were invisible in the only money view they read.
//
// These tests hold the two sides together in BOTH directions, and they assert the OUTPUT of the
// read surface rather than any property of a fixture.
//
// "Both directions" was once only half true. Enumerating ValueStampOperationTypes.All proves every
// DECLARED type resolves on the write side; nothing enumerated the write side, so a stray
// `case "Foo":` in BlockchainOperationManager compiled green and reproduced the exact
// contradiction — which is how Exchange and then IssueUnits drifted. The write side is now a TABLE
// whose keys are enumerable, so the missing direction is a real assertion below rather than a
// promise in a comment.

using System.Numerics;
using System.Reflection;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Analytics;
using FluentAssertions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Analytics;

public sealed class StampableSetDriftTests
{
    private static readonly Guid SubjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime Settled = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Types the write side deliberately has no reader for. Not an exhaustive list, and it no
    /// longer needs to be: the table's keys are enumerated above, so an undeclared type gaining a
    /// reader fails there rather than depending on somebody having listed it here.
    /// </summary>
    private static readonly string[] DeliberatelyUnstampable =
    {
        "DeployContract",
        "CallContract",
        "Composite",
    };

    // ── The two sides, held together ──────────────────────────────────────────

    [Fact]
    public void The_write_sides_table_is_exactly_the_shared_set_in_both_directions()
    {
        // The direction a switch statement could not hold at all: a reader taught to the write
        // side for a type the shared set never learned. The read surface answers membership from
        // that set, so such a row reads back "pricing": "Priced" beside "isStampable": false —
        // one row contradicting itself on a financial surface, twice shipped.
        WriteSideOperationTypes().Should().BeEquivalentTo(
            ValueStampOperationTypes.All,
            "the write side's reader table and the shared set are ONE list read from two places; "
            + "a type on either side alone is the drift this file exists to end");
    }

    [Fact]
    public void The_write_sides_table_refuses_to_exist_unless_it_matches_the_set()
    {
        // The guard is in the production type, not only here: BuildStampableAmountReaders throws
        // rather than returning a table that disagrees with the set. This asserts the guard is
        // reachable and passing — a table that disagreed would surface as a
        // TypeInitializationException out of the reflection below, not as a green test.
        var act = () => WriteSideOperationTypes();

        act.Should().NotThrow(
            "BlockchainOperationManager.BuildStampableAmountReaders refuses to initialise when the "
            + "write side and the shared set disagree, so this failing means they do");
    }

    [Theory]
    [MemberData(nameof(EveryStampableType))]
    public void Every_declared_stampable_type_actually_resolves_an_amount_on_the_write_side(string type)
    {
        // The direction that catches "somebody added a type to the shared set and never taught
        // BlockchainOperationManager how to read its quantity", which would stamp a row with a
        // fabricated zero or refuse it while the read surface promised a price.
        WriteSideResolves(WellFormed(type)).Should().BeTrue(
            $"'{type}' is declared in {nameof(ValueStampOperationTypes)}.{nameof(ValueStampOperationTypes.All)}, "
            + "so TryResolveStampableAmount must know which parameter carries its quantity");
    }

    [Theory]
    [MemberData(nameof(EveryDeliberatelyUnstampableType))]
    public void A_type_outside_the_set_resolves_nothing_and_reads_back_unstampable(string type)
    {
        WriteSideResolves(WellFormed(type)).Should().BeFalse(
            $"'{type}' is not in the shared set, so the write side must not stamp it");

        ValueStampOperationTypes.Contains(type).Should().BeFalse(
            "the read side answers membership from the same set the write side is held to");
    }

    // ── The regression the drift actually caused ──────────────────────────────

    [Fact]
    public async Task An_issuance_reads_back_stampable_rather_than_priced_and_unstampable_at_once()
    {
        var service = ServiceReturning(Issuance(amount: "500"));

        var page = await service.GetTransactionsAsync(SubjectId, new AnalyticsQueryRequest());

        page.IsError.Should().BeFalse(page.Message);
        var entry = page.Result!.Items.Should().ContainSingle().Subject;
        entry.IsStampable.Should().BeTrue(
            "an issuance gives up one identifiable quantity, and the write side stamps it — "
            + "reading it back unstampable is the exact contradiction the shared set exists to stop");
        entry.Pricing.Should().Be(PricingStatus.Priced);
    }

    [Fact]
    public async Task Units_issued_to_a_person_appear_in_that_persons_own_position()
    {
        // The thesis test. A sponsor or a worker who was paid in units reads ONE surface, and
        // before this an issuance contributed 0 to it: the money arrived and the money view said
        // nothing had.
        var service = ServiceReturning(Issuance(amount: "500"));

        var positions = await service.GetPositionsAsync(SubjectId, new AnalyticsQueryRequest());

        positions.IsError.Should().BeFalse(positions.Message);
        var position = positions.Result!.Items.Should().ContainSingle().Subject;
        position.Quantity.Should().Be(
            "500",
            "an issuance is the reserve paying units INTO this account, so it is a credit — "
            + "unclassified it contributed zero and the credit was invisible");
        position.UnclassifiedEntryCount.Should().Be(
            0, "an issuance has a direction the log determines; it is not an unknown");
    }

    [Fact]
    public async Task An_issuance_credits_where_a_transfer_out_debits()
    {
        // Direction is a separate decision from membership, and getting it backwards would be
        // worse than leaving it unclassified. Both rows are the same shape and the same asset.
        var service = ServiceReturning(
            Issuance(amount: "500"),
            Transfer(amount: "200"));

        var positions = await service.GetPositionsAsync(SubjectId, new AnalyticsQueryRequest());

        positions.Result!.Items.Should().ContainSingle()
            .Which.Quantity.Should().Be(
                "300", "500 paid in less 200 sent out — the two directions must be opposite");
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    public static TheoryData<string> EveryStampableType()
    {
        var data = new TheoryData<string>();
        foreach (var type in ValueStampOperationTypes.All)
            data.Add(type);
        return data;
    }

    public static TheoryData<string> EveryDeliberatelyUnstampableType()
    {
        var data = new TheoryData<string>();
        foreach (var type in DeliberatelyUnstampable)
            data.Add(type);
        return data;
    }

    /// <summary>
    /// The operation types the write side actually has a reader for, read off the REAL table.
    /// </summary>
    private static IReadOnlyCollection<string> WriteSideOperationTypes()
    {
        var field = typeof(BlockchainOperationManager).GetField(
            "StampableAmountReaders",
            BindingFlags.NonPublic | BindingFlags.Static);

        field.Should().NotBeNull(
            "the write side of the value stamp is the StampableAmountReaders table; if it was "
            + "renamed or turned back into a switch this guard must be repointed rather than "
            + "deleted — a switch's arms are enumerable by nothing");

        var table = field!.GetValue(null) as System.Collections.IDictionary;
        table.Should().NotBeNull(
            "the table must stay a dictionary keyed by operation type — that is what makes the "
            + "write side's own vocabulary enumerable, and a switch is not");

        return table!.Keys.Cast<string>().ToList();
    }

    /// <summary>
    /// Invokes the write side's own private resolver. Reflection is the point: the guard has to
    /// read the REAL decision, not a copy of it made in a test.
    /// </summary>
    private static bool WriteSideResolves(BlockchainOperation operation)
    {
        var method = typeof(BlockchainOperationManager).GetMethod(
            "TryResolveStampableAmount",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull(
            "the write side of the value stamp is TryResolveStampableAmount; if it was renamed "
            + "this guard must be repointed rather than deleted");

        var args = new object?[] { operation, null };
        return (bool)method!.Invoke(null, args)!;
    }

    /// <summary>
    /// A row carrying every quantity/asset parameter any arm of the switch reads, so the only
    /// thing that decides the answer is the operation TYPE.
    /// </summary>
    private static BlockchainOperation WellFormed(string type)
    {
        var operation = new BlockchainOperation
        {
            Id = Guid.NewGuid(),
            UserId = SubjectId,
            OperationType = type,
            Status = OperationStatus.Completed,
            Amount = RailAmount.OfChainAsset(new BigInteger(100)),
            CreatedDate = Settled,
        };

        operation.Parameters["Amount"] = "100";
        operation.Parameters["AmountIn"] = "100";
        operation.Parameters["AssetId"] = "1001";
        operation.Parameters["TokenId"] = "1001";
        operation.Parameters["TokenIn"] = "1001";
        operation.Parameters["SourceTokenId"] = "1001";
        operation.Parameters["ChainType"] = "Algorand";

        return operation;
    }

    private static BlockchainOperation Issuance(string amount) =>
        Shaped(AzoaScopes.IssueUnitsOperationType, amount);

    private static BlockchainOperation Transfer(string amount) =>
        Shaped(ValueStampOperationTypes.Transfer, amount);

    private static BlockchainOperation Shaped(string type, string amount)
    {
        var operation = new BlockchainOperation
        {
            Id = Guid.NewGuid(),
            UserId = SubjectId,
            OperationType = type,
            Status = OperationStatus.Completed,
            Amount = RailAmount.OfChainAsset(BigInteger.Parse(amount)),
            CreatedDate = Settled,
            ValueStamp = ValueStamp.FromPersisted(
                "USD", "25000", rate: "250", rateAsOf: Settled, rateSource: "test"),
        };

        operation.Parameters["Amount"] = amount;
        operation.Parameters["ChainType"] = "Algorand";
        operation.Parameters["SourceTokenId"] = "1001";

        return operation;
    }

    private static AnalyticsReadService ServiceReturning(params BlockchainOperation[] operations)
    {
        var store = new Mock<IBlockchainOperationStore>(MockBehavior.Strict);
        var rows = operations.Cast<IBlockchainOperation>().ToList();
        var served = false;

        // The positions walk pages until one comes back short; serving the same page forever
        // would fold the same rows in until the scan cap.
        store
            .Setup(s => s.QueryAsync(It.IsAny<BlockchainOperationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (served)
                    return AZOAResult<IReadOnlyList<IBlockchainOperation>>.Success([]);

                served = true;
                return AZOAResult<IReadOnlyList<IBlockchainOperation>>.Success(rows);
            });

        return new AnalyticsReadService(store.Object);
    }
}
