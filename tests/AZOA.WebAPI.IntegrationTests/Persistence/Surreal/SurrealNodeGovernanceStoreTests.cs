using AZOA.WebAPI.Persistence.SurrealDb.Models;
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
    private HttpSurrealConnection _connection = null!;
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
        _connection = new HttpSurrealConnection(
            new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) },
            options);
        _store = new SurrealNodeGovernanceStore(new DefaultSurrealExecutor(_connection));

        await SurrealTestSchema.BootstrapAsync(
            _testNamespace,
            NodeGovernanceParameters.SchemaNameConst,
            NodeGovernanceAudit.SchemaNameConst);
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
        var actor = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(Guid.NewGuid()))!;
        var parameters = new NodeGovernanceParameters
        {
            Id = NodeGovernanceParameters.LocalId,
            AllowedChains = ["Algorand"],
            Version = 1,
            UpdatedByAvatarId = actor,
            UpdatedAt = now,
        };
        var invalidAudit = new NodeGovernanceAudit
        {
            Id = SurrealId.ToSurrealId(Guid.NewGuid()),
            Action = string.Empty,
            ActorAvatarId = actor,
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
