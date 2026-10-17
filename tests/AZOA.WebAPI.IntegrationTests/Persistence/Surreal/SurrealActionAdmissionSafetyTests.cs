using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Sagas;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Models.Signing;
using RunRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionRun;
using NodeRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNodeExecution;
using NftRow = AZOA.WebAPI.Persistence.SurrealDb.Models.NftLifecycle;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealActionAdmissionSafetyTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealActionNodeExecutionStore _nodes = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "action_run", "action_node_execution", "saga_steps", "nft_lifecycle", "user", "wallet");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
        _nodes = new(_executor);
    }

    public async Task DisposeAsync()
    {
        try { await SurrealTestSchema.DropAsync(_namespace); }
        finally { _connection?.Dispose(); }
    }

    [Fact]
    public async Task FailureAfterSchedulerSnapshotFencesSuspendedSiblingAdmission()
    {
        var (run, actor) = await Run();
        var pending = await Node(run, ActionNodeState.Pending);
        var sibling = await Node(run, ActionNodeState.Running);
        pending.FrozenInput = Frozen(pending, actor);
        var capturedEpoch = 0L;
        sibling.State = ActionNodeState.Failed;
        sibling.Error = "authoritative failure";
        (await _nodes.UpdateAsync(sibling, ActionNodeState.Running)).IsError.Should().BeFalse();

        (await _nodes.TryAdmitAsync(pending, expectedRunVersion: capturedEpoch)).Result.Should().BeNull();
        (await _nodes.GetByIdAsync(pending.Id)).Result!.State.Should().Be(ActionNodeState.Pending);
        (await _executor.QuerySingleAsync<RunRow>(SurrealQuery<RunRow>.Key(run.ToString("N"))))!.Version.Should().Be(1);
        actor.Should().NotBeEmpty();
    }

    [Fact]
    public async Task OverlappingFreshAdmissionsConsumeOneSchedulerEpoch()
    {
        var (run, actor) = await Run();
        var pending = await Node(run, ActionNodeState.Pending);
        pending.FrozenInput = Frozen(pending, actor);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _nodes.TryAdmitAsync(pending.Clone(), expectedRunVersion: 0))).WaitAsync(TimeSpan.FromSeconds(20));
        claims.Count(item => item.Result is not null).Should().Be(1);
        (await _nodes.GetByRunAndNodeAsync(run, pending.NodeId)).Result!.State.Should().Be(ActionNodeState.Running);
    }

    [Fact]
    public async Task UniqueWorkflowStepUsesSagaCodecAndSignalPreservesAdmissionIdentity()
    {
        var run = Guid.NewGuid(); var node = Guid.NewGuid();
        var admission = new SurrealActionWorkflowAdmissionStore(_executor);
        const string payload = "{\"fixture\":true}";
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => admission.EnqueueNodeAsync(run, node, payload)))
            .WaitAsync(TimeSpan.FromSeconds(20));
        var sagas = new SurrealSagaStore(_executor);
        (await sagas.StepExistsAsync(run.ToString(), node.ToString(), default)).Should().BeTrue();
        var id = Guid.ParseExact(ActionWorkflowIdentity.StepId(run, node), "N");
        var claimed = await sagas.TryClaimDueStepAsync(id, DateTime.UtcNow.AddSeconds(1), default);
        claimed.Should().NotBeNull();
        var gate = $"operator:{node:N}";
        (await sagas.ParkStepAsync(id, claimed!.ClaimedAt!.Value, gate, null, default)).Should().BeTrue();
        (await sagas.GetParkedStepAsync(run.ToString(), gate, default))!.Id.Should().Be(id);
        (await sagas.TrySignalAsync(run.ToString(), gate, "{\"fixture\":true,\"SignalPayload\":{}}", default)).Should().NotBeNull();
        (await sagas.TrySignalAsync(run.ToString(), gate, null, default)).Should().BeNull();
        await admission.EnqueueNodeAsync(run, node, payload);
        (await _executor.QuerySingleAsync<AZOA.WebAPI.Persistence.SurrealDb.Models.SagaSteps>(
            SurrealQuery<AZOA.WebAPI.Persistence.SurrealDb.Models.SagaSteps>.Key(id.ToString("N"))))!
            .AdmissionPayloadHash.Should().Be(CanonicalJson.HashCanonical(payload));
    }

    [Fact]
    public async Task CancelledWorkflowBlocksNftAdmissionAndFirstEgressButAllowsAdmittedRecoveryStateWrites()
    {
        var (run, actor) = await Run();
        var execution = await Node(run, ActionNodeState.Running);
        execution.FrozenInput = "{\"nft\":true}";
        (await _nodes.UpdateAsync(execution, ActionNodeState.Running)).IsError.Should().BeFalse();
        var authority = new ActionEffectAuthority(run, execution.NodeId, actor, null, CanonicalJson.HashCanonical(execution.FrozenInput));
        var original = await OriginalWorkflowAuthority(execution, authority);
        var command = new NftLifecycleCommand(actor, actor, original.Wallet.Id, original.Wallet.Id, "actor", "actor", new(), -1, Guid.Empty,
            ActionAuthority: authority, ActorAuthority: original);
        var json = JsonSerializer.Serialize(command);
        var row = new NftRow { Id = Guid.NewGuid().ToString("N"), ActorUserId = actor.ToString("N"), RecipientUserId = actor.ToString("N"),
            NftId = Guid.NewGuid().ToString("N"), Operation = "Mint", ChainType = "Algorand", Network = "Devnet", State = "Prepared",
            RequestJson = json, RequestHash = NftLifecycleIdentity.Fingerprint(json), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var nfts = new SurrealNftLifecycleStore(_executor);
        row = await nfts.ClaimAsync(row);
        var sent = JsonSerializer.Deserialize<NftRow>(JsonSerializer.Serialize(row))!;
        sent.Id = Guid.NewGuid().ToString("N");
        sent = await nfts.ClaimAsync(sent);
        sent.State = "Submitted";
        sent = (await nfts.UpdateAsync(sent, sent.Version, requireActionAuthority: true))!;
        sent.Should().NotBeNull();
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<RunRow>(run.ToString("N"))
            .Set(item => item.Status, RunRow.ActionRunStatusKind.Cancelled).Build())).EnsureAllOk();
        row.State = "Submitted";
        (await nfts.UpdateAsync(row, row.Version, requireActionAuthority: true)).Should().BeNull();
        (await nfts.GetAsync(row.Id))!.State.Should().Be("Prepared");
        sent.State = "NeedsOperator";
        (await nfts.UpdateAsync(sent, sent.Version)).Should().NotBeNull("previously authorized egress still accepts recovery evidence");
        row.Id = Guid.NewGuid().ToString("N");
        var delayedAdmission = () => nfts.ClaimAsync(row);
        await delayedAdmission.Should().ThrowAsync<ActionEffectAuthorityChangedException>();
    }

    /// <summary>Saves the node's original signing witness and owned wallet that NFT admission now fences.</summary>
    private async Task<SigningAdmissionAuthority> OriginalWorkflowAuthority(ActionNodeExecution execution, ActionEffectAuthority action)
    {
        var wallet = Guid.NewGuid();
        (await _executor.ExecuteAsync(SurrealWriter.Create(new AZOA.WebAPI.Persistence.SurrealDb.Models.User { Id = action.ActorUserId.ToString("N"),
            Username = action.ActorUserId.ToString("N"), Email = action.ActorUserId + "@test.invalid", PasswordHash = "fixture", IsActive = true }))).EnsureAllOk();
        (await _executor.ExecuteAsync(SurrealWriter.Create(new AZOA.WebAPI.Persistence.SurrealDb.Models.Wallet { Id = wallet.ToString("N"),
            UserId = "user:" + action.ActorUserId.ToString("N"), Address = "actor", ChainType = "Algorand" }))).EnsureAllOk();
        var credential = new SigningCredentialWitness(1, "Jwt", action.ActorUserId, null, null, DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1), null, null, [AzoaScopes.ActionExecute, AzoaScopes.NftMint], true, TokenUse: AzoaClaims.TokenUseLogin);
        var witness = new WorkflowSigningWitness(1, credential, action, [AzoaScopes.ActionExecute, AzoaScopes.NftMint], []);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<NodeRow>(execution.Id.ToString("N"))
            .Set(row => row.SigningAuthorityJson, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(witness))).Build())).EnsureAllOk();
        return new(1, "Workflow", credential, new(wallet, action.ActorUserId, "actor", "Algorand"), witness.RequiredScopes, witness.Grants, action);
    }

    private async Task<(Guid Run, Guid Actor)> Run()
    {
        var run = Guid.NewGuid(); var actor = Guid.NewGuid();
        (await _executor.ExecuteAsync(SurrealWriter.Create(new RunRow { Id = run.ToString("N"),
            ActionId = "action:" + Guid.NewGuid().ToString("N"), UserId = "user:" + actor.ToString("N"),
            Status = RunRow.ActionRunStatusKind.Running, StartedAt = DateTimeOffset.UtcNow }))).EnsureAllOk();
        return (run, actor);
    }

    private async Task<ActionNodeExecution> Node(Guid run, ActionNodeState state)
    {
        var node = new ActionNodeExecution { Id = Guid.NewGuid(), RunId = run, NodeId = Guid.NewGuid(), State = state, StartedAt = DateTime.UtcNow };
        (await _nodes.CreateAsync(node)).IsError.Should().BeFalse();
        return node;
    }

    private static string Frozen(ActionNodeExecution node, Guid actor)
        => CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new ActionFrozenNodeInput(1, node.RunId, node.NodeId,
            ActionNodeType.HolonGet, actor, null, JsonSerializer.SerializeToElement(new { }),
            new Dictionary<Guid, ActionNodeExecution>(), new Dictionary<Guid, ActionNodeExecution>())));
}
