using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Services.Hold;

public sealed class HoldLocalCommandPlanTests
{
    [Fact]
    public void ChildInheritsOneLiabilityAndCannotRefundTheParent()
    {
        var (intent, reserve) = Input();
        var opened = HoldLocalCommandPlan.Create(intent, null, reserve, DateTimeOffset.UtcNow).Result!;
        reserve.Partitions = opened.Reserve!.Partitions;
        reserve.Version++;
        var chain = intent with { RequestKey = "chain", Kind = "ReleaseIntoHold", Definition = null,
            NextHoldId = "child", NextBeneficiaryUserId = Guid.NewGuid(),
            NextReleaseNotBefore = intent.Definition!.ReleaseNotBefore!.Value.AddDays(1) };
        var continued = HoldLocalCommandPlan.Create(chain, opened.Result, reserve, DateTimeOffset.UtcNow).Result!;
        continued.Reserve.Should().BeNull();
        continued.Result.State.Should().Be("ReleasedIntoHold");
        continued.Child!.InheritsReserveCommitment.Should().BeTrue();
        continued.Child.Amount.Should().Be("400");
        opened.Result.State.Should().Be("Held");
        var refund = intent with { RequestKey = "refund", Kind = "Refund", Definition = null };
        HoldLocalCommandPlan.Create(refund, continued.Result, reserve, DateTimeOffset.UtcNow).IsError.Should().BeTrue();
        var childRefund = refund with { HoldId = "child" };
        var returned = HoldLocalCommandPlan.Create(childRefund, continued.Child, reserve, DateTimeOffset.UtcNow).Result!;
        returned.Reserve!.Counters.Held.Should().Be(0);
        returned.Reserve.Counters.Available.Should().Be(1000);
    }

    [Theory]
    [InlineData("short-window")]
    [InlineData("different-owner")]
    [InlineData("different-protocol")]
    [InlineData("tampered-liability")]
    public void ChangedCustodyOrWindowCannotProduceAChild(string change)
    {
        var (intent, reserve) = Input();
        var opened = HoldLocalCommandPlan.Create(intent, null, reserve, DateTimeOffset.UtcNow).Result!;
        var chain = intent with { RequestKey = "chain", Kind = "ReleaseIntoHold", Definition = null,
            NextHoldId = "child", NextBeneficiaryUserId = Guid.NewGuid(),
            NextReleaseNotBefore = intent.Definition!.ReleaseNotBefore };
        if (change == "short-window") chain = chain with { NextReleaseNotBefore = chain.NextReleaseNotBefore!.Value.AddSeconds(-1) };
        if (change == "different-owner") chain = chain with { OwnerUserId = Guid.NewGuid() };
        if (change == "different-protocol") chain = chain with { Asset = chain.Asset with { ProtocolIdentity = "other" } };
        if (change == "tampered-liability") opened.Result.Amount = "399";
        HoldLocalCommandPlan.Create(chain, opened.Result, reserve, DateTimeOffset.UtcNow).IsError.Should().BeTrue();
    }

    [Theory]
    [InlineData("own-login", false)]
    [InlineData("custody-tenant", false)]
    [InlineData("other-tenant", true)]
    public void OnlyTheOwnersLoginOrTheCustodyTenantMayRefundAReserveHold(string caller, bool refused)
    {
        var (intent, reserve) = Input();
        var custody = Guid.NewGuid();
        intent = intent with { TenantId = custody, CustodyTenantId = custody };
        var opened = HoldLocalCommandPlan.Create(intent, null, reserve, DateTimeOffset.UtcNow).Result!;
        reserve.Partitions = opened.Reserve!.Partitions;
        reserve.Version++;
        var refund = intent with { RequestKey = "refund", Kind = "Refund", Definition = null,
            TenantId = caller switch { "own-login" => null, "custody-tenant" => custody, _ => Guid.NewGuid() } };

        HoldLocalCommandPlan.Create(refund, opened.Result, reserve, DateTimeOffset.UtcNow).IsError.Should().Be(refused);
    }

    [Fact]
    public void FunderFundingCannotBecomeAnUnpaidReserveOpening()
    {
        var (intent, reserve) = Input();
        intent = intent with { Definition = intent.Definition! with { FundingSource = "Funder", FunderUserId = intent.ActorUserId } };
        HoldLocalCommandPlan.Create(intent, null, reserve, DateTimeOffset.UtcNow).IsError.Should().BeTrue();
    }

    private static (HoldCommandIntent, PlatformReserveLedger) Input()
    {
        var owner = Guid.NewGuid();
        var asset = new HoldAssetIdentity("Algorand", ChainNetwork.Devnet, "genesis", "42", "reserve");
        var definition = new HoldDefinition(asset, "400", "Reserve", Guid.NewGuid(), null, "contributors",
            DateTimeOffset.UtcNow.AddDays(1), false, "work");
        var intent = new HoldCommandIntent(1, "open", "Open", "parent", owner, owner, null, null, asset, definition);
        var reserve = new PlatformReserveLedger { Id = "reserve", Chain = "Algorand", Network = "Devnet",
            ProtocolIdentity = "genesis", AssetId = "42", ReserveAddress = "reserve", TotalSupply = "1000",
            Partitions = ReservePartitionCodec.Encode(new Dictionary<string, ReservePartitionCounters>
                { ["contributors"] = ReservePartitionCounters.Declaring(1000) }) };
        return (intent, reserve);
    }
}
