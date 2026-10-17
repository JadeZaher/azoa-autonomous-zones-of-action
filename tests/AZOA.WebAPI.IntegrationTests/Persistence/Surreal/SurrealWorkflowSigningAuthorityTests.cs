using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions.Workflow;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using UserRow = AZOA.WebAPI.Persistence.SurrealDb.Models.User;
using RunRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionRun;
using GrantRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;
using NodeRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNodeExecution;
using AuthorizationRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionSigningAuthorization;
using StepRow = AZOA.WebAPI.Persistence.SurrealDb.Models.SagaSteps;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealWorkflowSigningAuthorityTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealActionNodeExecutionStore _nodes = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "consent_grant", "action_run", "action_node_execution", "action_signing_authorization", "saga_steps");
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
    public async Task SplitOriginalGrantsAdmitOneNodeAndPersistItsExactAuthority()
    {
        var (node, witness) = await SeedAsync();
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _nodes.TryAdmitAsync(node.Clone(), expectedRunVersion: 0)))
            .WaitAsync(TimeSpan.FromSeconds(20));
        claims.Count(result => result.Result is not null).Should().Be(1);
        var saved = (await _nodes.GetByIdAsync(node.Id)).Result!;
        saved.SigningAuthorityJson.Should().Be(node.SigningAuthorityJson);
        JsonSerializer.Deserialize<WorkflowSigningWitness>(saved.SigningAuthorityJson!)!.Grants.Select(grant => grant.Id)
            .Should().BeEquivalentTo(witness.Grants.Select(grant => grant.Id));
        JsonSerializer.Serialize(saved).Should().NotContain("SigningAuthority").And.NotContain("StoredApiKeyScopes");
    }

    [Theory]
    [InlineData("revoked-original")]
    [InlineData("changed-watermark")]
    [InlineData("widened-credential")]
    [InlineData("missing-original")]
    public async Task DelayedAdmissionCannotSubstituteCurrentAuthorityForItsOriginalWitness(string change)
    {
        var (node, witness) = await SeedAsync();
        if (change == "revoked-original")
        {
            await Execute(SurrealWriter.UpdateOnly<GrantRow>(witness.Grants[1].Id.ToString("N"))
                .Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build());
            await Execute(SurrealWriter.Create(new GrantRow { Id = Guid.NewGuid().ToString("N"),
                GrantorUserId = $"user:{witness.Credential.ActorUserId:N}", TenantId = $"user:{witness.Credential.TenantId:N}",
                Scopes = AzoaScopes.TokenCreateSign, GrantedAt = DateTimeOffset.UtcNow, Origin = "UserExplicit" }));
        }
        else if (change == "changed-watermark")
            await Execute(SurrealWriter.UpdateOnly<UserRow>(witness.Credential.ActorUserId.ToString("N"))
                .Set(row => row.AuthNotBefore, DateTimeOffset.UtcNow.AddSeconds(1)).Build());
        else if (change == "missing-original")
            await Execute(SurrealWriter.UpdateOnly<RunRow>(node.RunId.ToString("N")).Unset(row => row.AuthenticationWitnessJson).Build());
        else
        {
            var narrowed = witness.Credential with { Scopes = [AzoaScopes.ActionExecute] };
            await Execute(SurrealWriter.UpdateOnly<RunRow>(node.RunId.ToString("N"))
                .Set(row => row.AuthenticationWitnessJson, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(narrowed))).Build());
        }
        (await _nodes.TryAdmitAsync(node, expectedRunVersion: 0)).Result.Should().BeNull();
        (await _nodes.GetByIdAsync(node.Id)).Result!.State.Should().Be(ActionNodeState.Pending);
        (await _executor.QuerySingleAsync<RunRow>(SurrealQuery<RunRow>.Key(node.RunId.ToString("N"))))!.Version.Should().Be(0,
            "authority refusal rolls back the scheduler epoch and the node claim together");
    }

    [Fact]
    public async Task MissingWitnessCannotAdmitAnEconomicEffectButAtomicRejectionIsRecoverable()
    {
        var (node, _) = await SeedAsync();
        node.SigningAuthorityJson = null;
        (await _nodes.TryAdmitAsync(node.Clone(), expectedRunVersion: 0)).Result.Should().BeNull();
        node.AdmissionRejection = "The immutable token total is invalid; no handler was dispatched.";
        node.ExternalOperation = null;
        var rejected = await _nodes.TryAdmitAsync(node, expectedRunVersion: 0);
        rejected.Result.Should().NotBeNull();
        (await _executor.QuerySingleAsync<NodeRow>(SurrealQuery<NodeRow>.Key(node.Id.ToString("N"))))!
            .AdmissionRejection.Should().Be(node.AdmissionRejection);
    }

    [Fact]
    public async Task ExplicitParticipantAuthorizationPreservesLegacyRunAndReplaysWithoutWakingLaterStep()
    {
        var (audit, witness, node, step) = await ParkedLegacyAsync();
        var store = new SurrealActionSigningAuthorizationStore(_executor);
        var authorized = await store.AuthorizeAsync(audit, witness, node, step, default);
        authorized.IsError.Should().BeFalse(authorized.Message);
        var run = (await _executor.QuerySingleAsync<RunRow>(SurrealQuery<RunRow>.Key(node.RunId.ToString("N"))))!;
        run.AuthenticationWitnessJson.Should().BeNull("new consent must not fabricate historical admission evidence");
        var saved = (await _nodes.GetByIdAsync(node.Id)).Result!;
        saved.SigningAuthorityJson.Should().Be(audit.WitnessJson);
        var admitted = await _nodes.TryAdmitAsync(saved, expectedRunVersion: run.Version);
        admitted.Result.Should().NotBeNull("the independently audited exact authorization can admit the unchanged node");
        var sagas = new SurrealSagaStore(_executor);
        var stepId = Guid.ParseExact(step.Id, "N");
        var claimed = await sagas.TryClaimDueStepAsync(stepId, DateTime.UtcNow.AddSeconds(1), default);
        claimed.Should().NotBeNull();
        await sagas.ParkStepAsync(stepId, claimed!.ClaimedAt!.Value, $"participant:{node.NodeId:N}", null, default);
        (await store.AuthorizeAsync(audit, witness, node, step, default)).IsError.Should().BeFalse();
        (await sagas.GetAsync(stepId, default))!.Status.Should().Be(StepStatus.Parked);
        (await _executor.QueryAsync<AuthorizationRow>(SurrealQuery<AuthorizationRow>.From())).Should().ContainSingle();
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("grant")]
    [InlineData("version")]
    [InlineData("actor")]
    [InlineData("gate")]
    public async Task ParticipantAuthorizationCannotOutrunRevocationCancellationOrChangedOperation(string change)
    {
        var (audit, witness, node, step) = await ParkedLegacyAsync();
        if (change == "cancel")
            await Execute(SurrealWriter.UpdateOnly<RunRow>(audit.RunId).Set(row => row.Status, RunRow.ActionRunStatusKind.Cancelled).Build());
        else if (change == "grant")
            await Execute(SurrealWriter.UpdateOnly<GrantRow>(witness.Grants[0].Id.ToString("N")).Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build());
        else if (change == "version")
            await Execute(SurrealWriter.UpdateOnly<NodeRow>(audit.ExecutionId).Set(row => row.Version, node.Version + 1).Build());
        else if (change == "actor") audit.ActorId = Guid.NewGuid().ToString("N");
        else await Execute(SurrealWriter.UpdateOnly<StepRow>(step.Id).Set(row => row.GateId, SagaOpaqueKey.Encode("different" )).Build());
        var store = new SurrealActionSigningAuthorizationStore(_executor);
        (await store.AuthorizeAsync(audit, witness, node, step, default)).IsError.Should().BeTrue();
        (await store.GetAsync(audit.Id, default)).Should().BeNull();
        (await _nodes.GetByIdAsync(node.Id)).Result!.SigningAuthorityJson.Should().BeNull();
        (await store.GetStepAsync(node.RunId, node.NodeId, default))!.Status.Should().Be(StepRow.StepStatus.Parked);
    }

    private async Task<(AuthorizationRow Audit, WorkflowSigningWitness Witness, ActionNodeExecution Node, StepRow Step)> ParkedLegacyAsync()
    {
        var (candidate, original) = await SeedAsync();
        candidate.SigningAuthorityJson = null;
        candidate.RecoveryState = "NeedsParticipantAuthorization";
        var saved = await _nodes.UpdateAsync(candidate, ActionNodeState.Pending);
        saved.IsError.Should().BeFalse(saved.Message);
        candidate = saved.Result!;
        await Execute(SurrealWriter.UpdateOnly<RunRow>(candidate.RunId.ToString("N")).Unset(row => row.AuthenticationWitnessJson)
            .Set(row => row.Status, RunRow.ActionRunStatusKind.AwaitingReconciliation).Build());
        var run = (await _executor.QuerySingleAsync<RunRow>(SurrealQuery<RunRow>.Key(candidate.RunId.ToString("N"))))!;
        var payload = new ActionStepPayload(candidate.RunId, SurrealRecordGuid.Parse(run.ActionId), original.Credential.ActorUserId, candidate.NodeId);
        await new SurrealActionWorkflowAdmissionStore(_executor).EnqueueNodeAsync(candidate.RunId, candidate.NodeId, SagaStep<ActionStepPayload>.Serialize(payload));
        var sagas = new SurrealSagaStore(_executor);
        var stepId = Guid.ParseExact(ActionWorkflowIdentity.StepId(candidate.RunId, candidate.NodeId), "N");
        var claimed = await sagas.TryClaimDueStepAsync(stepId, DateTime.UtcNow.AddSeconds(1), default);
        claimed.Should().NotBeNull();
        (await sagas.ParkStepAsync(stepId, claimed!.ClaimedAt!.Value, $"participant:{candidate.NodeId:N}", null, default)).Should().BeTrue();
        var store = new SurrealActionSigningAuthorizationStore(_executor);
        var step = (await store.GetStepAsync(candidate.RunId, candidate.NodeId, default))!;
        var authorizationId = Guid.NewGuid();
        var witness = original with { AuthorizationId = authorizationId };
        var audit = new AuthorizationRow
        {
            Id = authorizationId.ToString("N"), ActorId = witness.Credential.ActorUserId.ToString("N"), TenantId = witness.Credential.TenantId?.ToString("N"),
            ExecutionId = candidate.Id.ToString("N"), RunId = candidate.RunId.ToString("N"), NodeId = candidate.NodeId.ToString("N"),
            ExpectedVersion = candidate.Version, IntentHash = witness.Action.IntentHash,
            WitnessJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(witness)), RequestHash = CanonicalJson.Hash(new { authorizationId }),
            Reason = "Explicitly authorize this unchanged frozen creation.", CreatedAt = DateTimeOffset.UtcNow,
        };
        return (audit, witness, candidate, step);
    }

    private async Task<(ActionNodeExecution Node, WorkflowSigningWitness Witness)> SeedAsync()
    {
        var actor = Guid.NewGuid(); var tenant = Guid.NewGuid(); var run = Guid.NewGuid(); var nodeId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (var id in new[] { actor, tenant })
            await Execute(SurrealWriter.Create(new UserRow { Id = id.ToString("N"), Username = id.ToString("N"),
                Email = id.ToString("N") + "@example.test", IsActive = true, CreatedDate = now }));
        var credential = new SigningCredentialWitness(1, "Jwt", actor, tenant, null, now, now.AddMinutes(10), null, null,
            [AzoaScopes.ActionExecute, AzoaScopes.TokenCreateSign], false, TokenUse: AzoaClaims.TokenUseChild);
        var grants = new List<SigningGrantWitness>();
        foreach (var scope in new[] { AzoaScopes.ActionExecute, AzoaScopes.TokenCreateSign })
        {
            var grant = new GrantRow { Id = Guid.NewGuid().ToString("N"), GrantorUserId = $"user:{actor:N}", TenantId = $"user:{tenant:N}",
                Scopes = scope, GrantedAt = now, Origin = "UserExplicit" };
            await Execute(SurrealWriter.Create(grant));
            grants.Add(new(Guid.ParseExact(grant.Id, "N"), scope, scope, now, null));
        }
        await Execute(SurrealWriter.Create(new RunRow { Id = run.ToString("N"), ActionId = $"action:{Guid.NewGuid():N}",
            UserId = $"user:{actor:N}", ActingTenantId = $"user:{tenant:N}", Status = RunRow.ActionRunStatusKind.Running,
            StartedAt = now, AuthenticationWitnessJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(credential)) }));
        var input = new ActionFrozenNodeInput(1, run, nodeId, ActionNodeType.FungibleTokenCreate, actor, tenant,
            JsonSerializer.SerializeToElement(new { name = "Fixture", total = "1", decimals = 0 }), new Dictionary<Guid, ActionNodeExecution>(), new Dictionary<Guid, ActionNodeExecution>());
        var frozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(input));
        var witness = new WorkflowSigningWitness(1, credential, new(run, nodeId, actor, tenant, CanonicalJson.HashCanonical(frozen)),
            [AzoaScopes.ActionExecute, AzoaScopes.TokenCreateSign], grants);
        var node = new ActionNodeExecution { Id = Guid.NewGuid(), RunId = run, NodeId = nodeId, State = ActionNodeState.Pending };
        (await _nodes.CreateAsync(node)).IsError.Should().BeFalse();
        node.FrozenInput = frozen;
        node.SigningAuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(witness));
        return (node, witness);
    }

    private async Task Execute(SurrealQuery query) => (await _executor.ExecuteAsync(query)).EnsureAllOk();
}
