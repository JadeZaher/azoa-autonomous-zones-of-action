using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Direct hold admission reads: liabilities and drawable reserve assets, scoped like the commands they back.</summary>
public sealed partial class SurrealHoldCommandTests
{
    [Fact]
    public async Task LiabilityReadsShowOwnReceivedAndCustodyLiabilitiesAndHideEveryoneElses()
    {
        // Rule (Services/Hold/AGENTS.md §liability-reads): what is owed to a person is theirs to see from any login;
        // what they merely sent under a tenant's custody is visible only under that tenant's credential.
        var other = Guid.NewGuid(); var tenant = Guid.NewGuid(); var otherTenant = Guid.NewGuid();
        await Save(Liability("own", _actor, _actor, null));
        await Save(Liability("received", other, _actor, null));
        await Save(Liability("custody", other, other, _actor));
        await Save(Liability("tenanted-received", _actor, _actor, tenant));
        await Save(Liability("tenanted-sent", _actor, other, tenant));
        await Save(Liability("other-tenant-sent", _actor, other, otherTenant));
        await Save(Liability("foreign", other, other, null));
        var reads = new HoldLiabilityReadService(_store, _store, new ReserveOptions());

        (await reads.ListAsync(_actor, null, 0, 20)).Result!.Items.Select(item => item.HoldId)
            .Should().BeEquivalentTo("own", "received", "custody", "tenanted-received");
        (await reads.ListAsync(_actor, tenant, 0, 20)).Result!.Items.Select(item => item.HoldId)
            .Should().BeEquivalentTo("tenanted-received", "tenanted-sent");
        (await reads.ListAsync(other, null, 0, 20)).Result!.Items.Select(item => item.HoldId)
            .Should().BeEquivalentTo("received", "custody", "foreign", "tenanted-sent", "other-tenant-sent");

        var own = (await reads.GetAsync("own", _actor, null)).Result!;
        own.Asset.HoldingAddress.Should().Be("reserve");
        own.CustodyTenantId.Should().BeNull();
        (await reads.GetAsync("custody", _actor, null)).Result!.CustodyTenantId.Should().Be(_actor);
        (await reads.GetAsync("tenanted-received", _actor, null)).Result!.CustodyTenantId.Should().Be(tenant);
        (await reads.GetAsync("foreign", _actor, null)).IsError.Should().BeTrue();
        (await reads.GetAsync("tenanted-sent", _actor, null)).IsError.Should().BeTrue();
        (await reads.GetAsync("other-tenant-sent", _actor, tenant)).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ReserveAssetReadsListOnlyWhatTheCallersOpenCouldDraw()
    {
        await Save(new PlatformReserveLedger { Id = PlatformReserveLedger.RecordIdFor("Algorand", "Devnet", "7"), Chain = "Algorand",
            Network = "Devnet", ProtocolIdentity = "genesis", AssetId = "7", ReserveAddress = "reserve", UnitName = "NODE",
            TotalSupply = "1000", Partitions = ReservePartitionCodec.Encode(new Dictionary<string, ReservePartitionCounters>
                { ["operator"] = ReservePartitionCounters.Declaring(500) }) });
        var reads = new HoldLiabilityReadService(_store, _store, new ReserveOptions());

        var owned = (await reads.ListReserveAssetsAsync(_actor, null, isNodeOperator: false)).Result!;
        owned.Should().ContainSingle().Which.Asset.AssetId.Should().Be("42");
        owned[0].Partitions.Should().ContainSingle(partition => partition.Name == "contributors" && partition.AvailableBaseUnits == "1000");
        (await reads.ListReserveAssetsAsync(Guid.NewGuid(), null, isNodeOperator: false)).Result.Should().BeEmpty();
        (await reads.ListReserveAssetsAsync(Guid.NewGuid(), Guid.NewGuid(), isNodeOperator: false)).Result
            .Should().BeEmpty("the node's own unit is operator-only under the default draw policy");
        (await reads.ListReserveAssetsAsync(_actor, null, isNodeOperator: true)).Result!
            .Should().ContainSingle().Which.Asset.AssetId.Should().Be("7");
    }

    private static HoldLedger Liability(string holdId, Guid owner, Guid beneficiary, Guid? custodyTenant) => new()
    {
        Id = holdId, HoldId = holdId, Chain = "Algorand", Network = "Devnet", ProtocolIdentity = "genesis",
        OwnerUserId = owner.ToString("N"), HoldingAddress = "reserve", AssetId = "42", Amount = "10", ReservePartition = "contributors",
        FundingSource = "Reserve", BeneficiaryUserId = "user:" + beneficiary.ToString("N"),
        TenantId = custodyTenant is { } tenant ? "user:" + tenant.ToString("N") : null, State = "Held",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };
}
