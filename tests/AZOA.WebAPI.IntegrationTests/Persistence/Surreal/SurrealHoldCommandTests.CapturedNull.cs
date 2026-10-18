using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealHoldCommandTests
{
    /// <summary>Captured-null negative for <c>FindOwnedHoldAsync</c>: a null tenant matches only tenant-less liabilities.</summary>
    [Fact]
    public async Task OwnedHoldLookupKeepsTenantlessAndTenantHeldLiabilitiesApart()
    {
        var intent = Open();
        (await _store.CommitLocalAsync(intent, Authority())).Should().NotBeNull();
        var holdRecordId = HoldCommandIdentity.Hold(intent);
        var tenant = Guid.NewGuid();
        Task<HoldLedger?> Find(Guid owner, Guid? scope) =>
            _store.FindOwnedHoldAsync(owner, scope, "Algorand", ChainNetwork.Devnet, intent.HoldId);

        (await Find(_actor, null))!.HoldId.Should().Be(intent.HoldId);
        (await Find(_actor, tenant)).Should().BeNull();

        await Execute(SurrealWriter.UpdateOnly<HoldLedger>(holdRecordId).Where(seed => true)
            .Set(row => row.TenantId, SurrealLink.ToLink("user", tenant.ToString("N"))).Build());

        (await Find(_actor, null)).Should().BeNull();
        (await Find(_actor, tenant))!.HoldId.Should().Be(intent.HoldId);
        (await Find(Guid.NewGuid(), tenant)).Should().BeNull();
    }
}
