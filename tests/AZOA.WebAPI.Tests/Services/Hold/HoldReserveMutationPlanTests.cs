using System.Numerics;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Services.Hold;

public sealed class HoldReserveMutationPlanTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenThenRefundOrReleaseConservesCommitmentsWithoutMutatingSnapshot(bool release)
    {
        var original = Ledger();
        var originalJson = original.Partitions!.Value.GetRawText();
        var opening = HoldReserveMutationPlan.Create(original, "contributors", ReserveDrawKind.Hold, "400", "protocol").Result!;
        opening.Counters.Available.Should().Be(new BigInteger(600));
        original.Partitions.Value.GetRawText().Should().Be(originalJson);
        var held = Ledger(); held.Partitions = opening.Partitions; held.Version = 1;
        var settlement = HoldReserveMutationPlan.Create(held, "contributors",
            release ? ReserveDrawKind.ReleaseHold : ReserveDrawKind.CancelHold, "400", "protocol").Result!;
        settlement.Counters.Held.Should().Be(BigInteger.Zero);
        settlement.Counters.Issued.Should().Be(release ? new BigInteger(400) : BigInteger.Zero);
        settlement.Counters.Available.Should().Be(new BigInteger(release ? 600 : 1000));
        settlement.IdentityHash.Should().Be(opening.IdentityHash);
        settlement.ExpectedVersion.Should().Be(1);
    }

    [Theory]
    [InlineData("1001", "protocol")]
    [InlineData("0400", "protocol")]
    [InlineData("400", "other-network")]
    public void InvalidBudgetOrProtocolProducesNoPlan(string amount, string protocol)
        => HoldReserveMutationPlan.Create(Ledger(), "contributors", ReserveDrawKind.Hold, amount, protocol).IsError.Should().BeTrue();

    private static PlatformReserveLedger Ledger() => new()
    {
        Id = "reserve", Chain = "Algorand", Network = "Devnet", ProtocolIdentity = "protocol", AssetId = "123",
        ReserveAddress = "reserve-address", UnitName = "UNIT", TotalSupply = "1000",
        Partitions = ReservePartitionCodec.Encode(new Dictionary<string, ReservePartitionCounters>
            { ["contributors"] = ReservePartitionCounters.Declaring(1000) }),
    };
}
