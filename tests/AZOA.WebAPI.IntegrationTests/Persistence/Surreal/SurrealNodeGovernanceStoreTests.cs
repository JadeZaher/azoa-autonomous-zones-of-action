using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealNodeGovernanceStoreTests : IAsyncLifetime
{
    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealNodeGovernanceStore _store = null!;
    private ISurrealConnection _connection = null!;
    private bool _surrealAvailable;

    public async Task InitializeAsync()
    {
        _surrealAvailable = await ProbeSurrealAsync();
        if (!_surrealAvailable)
            return;

        var options = new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _testNamespace,
            Database = "test",
            User = SurrealTestDefaults.User,
            Password = SurrealTestDefaults.Password,
        };
        _connection = new SurrealDbNetConnection(options);
        _store = new SurrealNodeGovernanceStore(new DefaultSurrealExecutor(_connection));

        await SurrealTestSchema.BootstrapAsync(
            _testNamespace,
            NodeGovernanceParameters.SchemaNameConst,
            NodeGovernanceAudit.SchemaNameConst,
            "node_governance_policy_lock");
    }

    public async Task DisposeAsync()
    {
        if (!_surrealAvailable || _connection is null)
            return;

        try
        {
            await SurrealTestSchema.DropAsync(_testNamespace);
        }
        catch
        {
            // Best-effort test isolation cleanup.
        }
        finally
        {
            _connection.Dispose();
        }
    }

    [SkippableFact]
    public async Task AuditCreateFailure_RollsBackPolicyWrite()
    {
        Skip.IfNot(
            _surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTimeOffset.UtcNow;
        var actor = SurrealLink.ToLink("user", SurrealId.ToSurrealId(Guid.NewGuid()))!;
        var parameters = new NodeGovernanceParameters
        {
            Id = NodeGovernanceParameters.LocalId,
            AllowedChains = ["Algorand"],
            Version = 1,
            UpdatedByUserId = actor,
            UpdatedAt = now,
        };
        var invalidAudit = new NodeGovernanceAudit
        {
            Id = SurrealId.ToSurrealId(Guid.NewGuid()),
            Action = string.Empty,
            ActorUserId = actor,
            PreviousVersion = 0,
            NewVersion = 1,
            AllowedChains = ["Algorand"],
            OccurredAt = now,
        };

        Func<Task> act = async () => await _store.UpdateParametersWithAuditAsync(
            parameters,
            invalidAudit,
            expectedVersion: null);

        await act.Should().ThrowAsync<SurrealStatementException>();
        var loaded = await _store.GetParametersAsync();
        loaded.Result.Should().BeNull(
            "the policy UPSERT and audit CREATE are one rollback boundary");
        var audit = await _store.ListAuditAsync(10);
        audit.Result.Should().BeEmpty();
    }

    [Fact]
    public async Task AbsentPolicyWitnessCannotAuthorizeAnEffectAfterInitialPolicyCreation()
    {
        _surrealAvailable.Should().BeTrue("the integrated persistence gate requires SurrealDB");
        var executor = new DefaultSurrealExecutor(_connection);
        var before = await NodeGovernancePolicyFence.ReadVersionAsync(executor);
        before.Should().BeNull();
        var actor = SurrealLink.ToLink("user", Guid.NewGuid().ToString("N"))!;
        var now = DateTimeOffset.UtcNow;
        var policy = new NodeGovernanceParameters
        {
            AllowedAssetTypes = [], Version = 1, UpdatedByUserId = actor, UpdatedAt = now
        };
        var audit = new NodeGovernanceAudit
        {
            Id = Guid.NewGuid().ToString("N"), Action = "policy:update", ActorUserId = actor,
            PreviousVersion = 0, NewVersion = 1, AllowedAssetTypes = [], OccurredAt = now
        };
        var created = await _store.UpdateParametersWithAuditAsync(policy, audit, null);
        created.IsError.Should().BeFalse(created.Message);
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(NodeGovernancePolicyFence.Create(before));
        statements.Add(SurrealQuery.Of("COMMIT"));
        Func<Task> effect = async () =>
        {
            var response = await executor.ExecuteAsync(SurrealQuery.Combine(statements.ToArray()));
            response.EnsureAllOk();
        };
        await effect.Should().ThrowAsync<SurrealStatementException>().WithMessage("*NODE_GOVERNANCE_POLICY_CHANGED*");
    }

    private static async Task<bool> ProbeSurrealAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync(SurrealTestDefaults.Endpoint + "/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
