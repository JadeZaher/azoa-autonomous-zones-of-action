using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealDeploymentResourceFenceTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "action_graph_bundle", "holon", "local_entity_mutation_lock", "deployment_operation");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
    }
    public async Task DisposeAsync() { _connection?.Dispose(); await SurrealTestSchema.DropAsync(_namespace); }

    [Theory]
    [InlineData("root-private")]
    [InlineData("nested-version")]
    [InlineData("resource-private")]
    [InlineData("resource-content")]
    public async Task MutationAfterPreReadRollsBackWorkflowAdmissionEffect(string mutation)
    {
        var (actor, root, nested, resource, witness) = await Seed();
        switch (mutation)
        {
            case "root-private": root.IsPublic = false; await Write(SurrealWriter.Upsert(root)); break;
            case "nested-version": nested.DeploymentConfig = "new-artifact"; await Write(SurrealWriter.Upsert(nested)); break;
            case "resource-private": resource.IsPublic = false; await Write(SurrealWriter.Upsert(resource)); break;
            case "resource-content": resource.Metadata = JsonSerializer.SerializeToElement(new { amount = "999" }); await Write(SurrealWriter.Upsert(resource)); break;
        }
        var effectId = Guid.NewGuid().ToString("N");
        var transaction = () => CommitWitness(actor, witness, effectId);
        await transaction.Should().ThrowAsync<SurrealStatementException>().WithMessage("*ACTION_ADMISSION_DEPLOYMENT_CHANGED*");
        (await _executor.QuerySingleAsync<DeploymentOperation>(SurrealQuery<DeploymentOperation>.Key(effectId))).Should().BeNull();
    }

    [Fact]
    public async Task ExactContentAndAccessPermitAdmissionDespiteNewerRootGeneratedVersion()
    {
        var (actor, root, _, _, witness) = await Seed();
        root.DeploymentConfig = "a-new-root-generation";
        await Write(SurrealWriter.Upsert(root));
        var effect = Guid.NewGuid().ToString("N");
        await CommitWitness(actor, witness, effect);
        (await _executor.QuerySingleAsync<DeploymentOperation>(SurrealQuery<DeploymentOperation>.Key(effect))).Should().NotBeNull();
    }

    private async Task CommitWitness(Guid actor, DeploymentAuthorityWitness witness, string effectId)
    {
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(DeploymentAuthorityFence.Create(witness, actor));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(new DeploymentOperation { Id = effectId,
            ActorUserId = actor.ToString("N"), ArtifactId = "artifact", ArtifactHash = "hash", IntentHash = "intent", RequestJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }), "admitted_effect"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        await Write(SurrealQuery.Combine(statements.ToArray()));
    }

    private async Task<(Guid Actor, ActionGraphBundle Root, ActionGraphBundle Nested, Holon Resource, DeploymentAuthorityWitness Witness)> Seed()
    {
        var actor = Guid.NewGuid(); var owner = Guid.NewGuid(); var rootId = Guid.NewGuid(); var nestedId = Guid.NewGuid(); var resourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        var root = new ActionGraphBundle { Id = rootId.ToString("N"), Name = "root", UserId = SurrealLink.ToLink("user", owner.ToString("N")), IsActive = true, IsPublic = true, CreatedDate = now, DeploymentConfig = "root-artifact" };
        var nested = new ActionGraphBundle { Id = nestedId.ToString("N"), Name = "nested", UserId = root.UserId, IsActive = true, IsPublic = true, CreatedDate = now, DeploymentConfig = "nested-artifact" };
        var metadata = new Dictionary<string, string> { ["amount"] = "100" };
        var resource = new Holon { Id = resourceId.ToString("N"), Name = "resource", Description = "bound", UserId = root.UserId,
            ProviderName = "SurrealDB", AssetType = "fixture", Metadata = JsonSerializer.SerializeToElement(metadata), PeerHolonIds = [],
            CreatedDate = now, IsActive = true, IsPublic = true };
        await Write(SurrealWriter.Create(root)); await Write(SurrealWriter.Create(nested)); await Write(SurrealWriter.Create(resource));
        var witness = new DeploymentAuthorityWitness([new(rootId, owner, true, null), new(nestedId, owner, true, "nested-artifact")],
            [new(resourceId, resource.Name, resource.Description, null, owner, resource.ProviderName, null, resource.AssetType, null,
                metadata, [], now.UtcDateTime, null, true, null, null, true)]);
        return (actor, root, nested, resource, witness);
    }

    private async Task Write(SurrealQuery query) { var response = await _executor.ExecuteAsync(query); response.EnsureAllOk(); }
}
