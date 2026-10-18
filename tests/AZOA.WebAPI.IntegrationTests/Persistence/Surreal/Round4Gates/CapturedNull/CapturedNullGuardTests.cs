using AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Interleaving;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.CapturedNull;

/// <summary>
/// SurrealForge 1.3.0 binds a captured null as NONE, so `TenantId == tenant` with tenant = null now matches
/// tenant-less rows (it used to match nothing). These negatives pin that the flip only ever admits the caller's
/// own tenant-less rows: never a tenant-held row, never another actor's (round4-sql-sweep "Captured-null guard widening").
/// The Hold sites (SurrealHoldCommandStore.cs:55, HoldReleaseWorkflowFence.cs:35) are covered by SurrealHoldCommandTests.CapturedNull.cs.
/// </summary>
public sealed class CapturedNullGuardTests : IAsyncLifetime
{
    private InterleavingDatabase _database = null!;
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
        => _database = await InterleavingDatabase.CreateAsync("kyc_submission", "hold_release_mandate", "fungible_creation_operation");

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Kyc_submission_lookups_with_no_tenant_see_only_the_callers_direct_submission()
    {
        var store = new SurrealKycStore(_database.Executor);
        var tenantHeld = await SubmissionAsync(_actor, _tenant, KycStatus.PENDING);
        // Only a tenant-held submission exists: the direct lookup must not widen onto it.
        (await store.GetLatestSubmissionAsync(_actor, null)).Result.Should().BeNull();
        (await store.GetActiveSubmissionAsync(_actor, null)).Result.Should().BeNull();

        var direct = await SubmissionAsync(_actor, null, KycStatus.IN_REVIEW);
        await SubmissionAsync(_other, null, KycStatus.PENDING);

        (await store.GetLatestSubmissionAsync(_actor, null)).Result!.Id.Should().EndWith(direct);
        (await store.GetActiveSubmissionAsync(_actor, null)).Result!.Id.Should().EndWith(direct);
        (await store.GetActiveSubmissionAsync(_actor, _tenant)).Result!.Id.Should().EndWith(tenantHeld);
        (await store.GetLatestSubmissionAsync(Guid.NewGuid(), null)).Result.Should().BeNull("another subject's direct row is never a match");
    }

    [Fact]
    public async Task Release_mandate_owner_list_with_no_tenant_lists_only_the_actors_tenantless_mandates()
    {
        var store = new SurrealHoldReleaseMandateStore(_database.Executor);
        var own = await SaveAsync(new HoldReleaseMandate(), mandate => { mandate.ActorUserId = _actor.ToString("N"); mandate.TenantId = null; });
        var tenantHeld = await SaveAsync(new HoldReleaseMandate(), mandate => { mandate.ActorUserId = _actor.ToString("N"); mandate.TenantId = _tenant.ToString("N"); });
        await SaveAsync(new HoldReleaseMandate(), mandate => { mandate.ActorUserId = _other.ToString("N"); mandate.TenantId = null; });

        (await store.ListOwnerAsync(_actor, null, 0, 50)).Select(mandate => mandate.Id).Should().Equal(own);
        (await store.ListOwnerAsync(_actor, _tenant, 0, 50)).Select(mandate => mandate.Id).Should().Equal(tenantHeld);
    }

    [Fact]
    public async Task Fungible_participant_list_with_no_tenant_lists_only_the_actors_tenantless_operations()
    {
        var store = new SurrealFungibleCreationStore(_database.Executor);
        var own = await SaveAsync(new FungibleCreationOperation(), operation => { operation.ActorUserId = _actor.ToString("N"); operation.TenantId = null; });
        var tenantHeld = await SaveAsync(new FungibleCreationOperation(), operation => { operation.ActorUserId = _actor.ToString("N"); operation.TenantId = _tenant.ToString("N"); });
        await SaveAsync(new FungibleCreationOperation(), operation => { operation.ActorUserId = _other.ToString("N"); operation.TenantId = null; });

        (await store.ListParticipantAsync(_actor, null, 0, 50)).Select(operation => operation.Id).Should().Equal(own);
        (await store.ListParticipantAsync(_actor, _tenant, 0, 50)).Select(operation => operation.Id).Should().Equal(tenantHeld);
    }

    private async Task<string> SubmissionAsync(Guid user, Guid? tenant, KycStatus status)
        => await SaveAsync(new KycSubmission(), submission =>
        {
            submission.UserId = SurrealLink.ToLink("user", user.ToString("N"));
            submission.TenantId = tenant is { } scope ? SurrealLink.ToLink("user", scope.ToString("N")) : null;
            submission.Status = status;
            submission.SubmittedAt = DateTimeOffset.UtcNow;
        });

    private async Task<string> SaveAsync<T>(T row, Action<T> shape) where T : class, ISurrealRecord, new()
    {
        ModelValidRow.Fill(row);
        shape(row);
        (await _database.Executor.ExecuteAsync(SurrealWriter.Create(row))).EnsureAllOk();
        return (string)typeof(T).GetProperty("Id")!.GetValue(row)!;
    }
}
