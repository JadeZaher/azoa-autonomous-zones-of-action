using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealDeploymentAuthorityTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealDeploymentStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "deployment_operation", "action_run", "action_node_execution");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = _namespace, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new SurrealDeploymentStore(_executor);
    }

    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Theory]
    [InlineData(ActionRun.ActionRunStatusKind.Cancelled)]
    [InlineData(ActionRun.ActionRunStatusKind.Forked)]
    public async Task TerminalRunCannotAdmitSuspendedCreator(ActionRun.ActionRunStatusKind status)
    {
        var candidate = await SeedAsync();
        await SetRunStateAsync(candidate, status);
        (await _store.ClaimAsync(candidate)).Should().BeNull();
        (await _store.GetAsync(candidate.Id)).Should().BeNull();
    }

    [Theory]
    [InlineData("Preparing", false)]
    [InlineData("Submitted", true)]
    public async Task CancellationBetweenAdmissionAndNewEffectBlocksMutation(string state, bool authorized)
    {
        var candidate = await SeedAsync();
        var admitted = (await _store.ClaimAsync(candidate))!;
        if (authorized)
        {
            admitted.State = "Prepared";
            admitted.PreparedJson = "{\"transactionId\":\"saved-wire\"}";
            admitted = (await _store.UpdateAsync(admitted, admitted.Version))!;
        }
        var priorVersion = admitted.Version;
        await SetRunStateAsync(admitted, ActionRun.ActionRunStatusKind.Cancelled);
        admitted.State = state;
        admitted.SubmissionAuthorized = authorized;
        (await _store.UpdateAsync(admitted, priorVersion, requireActionAuthority: true)).Should().BeNull();
        var saved = (await _store.GetAsync(admitted.Id))!;
        saved.Version.Should().Be(priorVersion);
        saved.SubmissionAuthorized.Should().BeFalse();
        saved.State.Should().Be(authorized ? "Prepared" : "Admitted");
    }

    [Fact]
    public async Task AlreadyAuthorizedObservationRemainsDurableAfterCancellation()
    {
        var admitted = (await _store.ClaimAsync(await SeedAsync()))!;
        admitted.State = "Submitted";
        admitted.PreparedJson = "{\"transactionId\":\"authorized-wire\"}";
        admitted.SubmissionAuthorized = true;
        var authorized = (await _store.UpdateAsync(admitted, admitted.Version, requireActionAuthority: true))!;
        await SetRunStateAsync(authorized, ActionRun.ActionRunStatusKind.Cancelled);
        authorized.State = "Confirmed";
        authorized.EvidenceJson = "{\"transactionId\":\"authorized-wire\",\"finalized\":true}";
        var observed = await _store.UpdateAsync(authorized, authorized.Version);
        observed.Should().NotBeNull();
        observed!.State.Should().Be("Confirmed");
        observed.SubmissionAuthorized.Should().BeTrue();
        observed.EvidenceJson.Should().Be(authorized.EvidenceJson);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("tenant")]
    [InlineData("intent")]
    [InlineData("node")]
    public async Task ImmutableActionAuthorityMustMatchAtAdmission(string mismatch)
    {
        var candidate = await SeedAsync(withTenant: true);
        switch (mismatch)
        {
            case "actor": candidate.ActorUserId = Guid.NewGuid().ToString("N"); break;
            case "tenant": candidate.TenantId = Guid.NewGuid().ToString("N"); break;
            case "intent": candidate.ActionIntentHash = new string('0', 64); break;
            case "node": candidate.ActionNodeId = Guid.NewGuid().ToString("N"); break;
        }
        (await _store.ClaimAsync(candidate)).Should().BeNull();
        (await _store.GetAsync(candidate.Id)).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveAuthorityCanAdmitAndAuthorizeExactStoredWire(bool withTenant)
    {
        var candidate = await SeedAsync(withTenant);
        var admitted = (await _store.ClaimAsync(candidate))!;
        admitted.State = "Preparing";
        admitted = (await _store.UpdateAsync(admitted, admitted.Version, requireActionAuthority: true))!;
        admitted.Should().NotBeNull();
        admitted.State = "Prepared";
        admitted.PreparedJson = "{\"transactionId\":\"exact-wire\"}";
        admitted = (await _store.UpdateAsync(admitted, admitted.Version))!;
        (await _store.ListPendingAsync(100)).Should().BeEmpty();
        admitted.State = "Submitted";
        admitted.SubmissionAuthorized = true;
        var authorized = (await _store.UpdateAsync(admitted, admitted.Version, requireActionAuthority: true))!;
        authorized.PreparedJson.Should().Be(admitted.PreparedJson);
        (await _store.ListPendingAsync(100)).Should().ContainSingle(item => item.Id == admitted.Id);
    }

    private async Task<DeploymentOperation> SeedAsync(bool withTenant = false)
    {
        var now = DateTimeOffset.UtcNow;
        var actor = Guid.NewGuid().ToString("N");
        var tenant = withTenant ? Guid.NewGuid().ToString("N") : null;
        var run = new ActionRun
        {
            Id = Guid.NewGuid().ToString("N"), ActionId = Guid.NewGuid().ToString("N"), UserId = actor,
            ActingTenantId = tenant, Status = ActionRun.ActionRunStatusKind.Running, StartedAt = now,
        };
        var execution = new ActionNodeExecution
        {
            Id = Guid.NewGuid().ToString("N"), RunId = run.Id, NodeId = Guid.NewGuid().ToString("N"),
            State = ActionNodeExecution.ActionNodeState.Running, StartedAt = now, FrozenInput = "{\"Version\":1}",
        };
        (await _executor.ExecuteAsync(SurrealWriter.Create(run))).EnsureAllOk();
        (await _executor.ExecuteAsync(SurrealWriter.Create(execution))).EnsureAllOk();
        return new DeploymentOperation
        {
            Id = Guid.NewGuid().ToString("N"), ActorUserId = actor, TenantId = tenant ?? string.Empty,
            ArtifactId = "immutable-artifact", ArtifactHash = "immutable-artifact-hash", IntentHash = "intent-hash",
            RequestJson = "{}", ActionRunId = run.Id, ActionNodeId = execution.NodeId,
            ActionIntentHash = CanonicalJson.HashCanonical(execution.FrozenInput), CreatedAt = now, UpdatedAt = now,
        };
    }

    private async Task SetRunStateAsync(DeploymentOperation operation, ActionRun.ActionRunStatusKind state)
        => (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<ActionRun>(operation.ActionRunId!).Where(seed => true)
            .Set(row => row.Status, state).Build())).EnsureAllOk();
}
