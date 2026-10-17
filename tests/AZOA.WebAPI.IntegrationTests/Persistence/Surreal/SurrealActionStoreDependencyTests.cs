using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Action dependencies persist with the action (round 4): run-start dependency enforcement sees them after a reload.</summary>
public sealed class SurrealActionStoreDependencyTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private SurrealActionStore _actions = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "dapp_series", "action", "action_node", "action_edge", "action_dependency");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = _namespace, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        _actions = new(new DefaultSurrealExecutor(_connection));
    }

    public async Task DisposeAsync()
    {
        _connection.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Fact]
    public async Task Dependencies_round_trip_are_replaced_on_upsert_and_deleted_with_the_action()
    {
        var owner = Guid.NewGuid();
        var upstream = new ActionDefinition { Id = Guid.NewGuid(), Name = "upstream", UserId = owner };
        (await _actions.UpsertActionAsync(upstream)).IsError.Should().BeFalse();
        var action = new ActionDefinition
        {
            Id = Guid.NewGuid(), Name = "dependent", UserId = owner,
            Dependencies = [new ActionDependency { DependsOnActionId = upstream.Id, DependencyType = ActionDependencyType.Required }],
        };

        var saved = await _actions.UpsertActionAsync(action);

        saved.IsError.Should().BeFalse(saved.Message);
        var reloaded = (await _actions.GetActionAsync(action.Id)).Result!;
        reloaded.Dependencies.Should().ContainSingle().Which.Should().Match<ActionDependency>(dependency =>
            dependency.ActionId == action.Id && dependency.DependsOnActionId == upstream.Id
            && dependency.DependencyType == ActionDependencyType.Required && dependency.DependsOnNodeId == null);

        reloaded.Dependencies[0].DependencyType = ActionDependencyType.Optional;
        (await _actions.UpsertActionAsync(reloaded)).IsError.Should().BeFalse();
        (await _actions.GetActionAsync(action.Id)).Result!.Dependencies.Should().ContainSingle()
            .Which.DependencyType.Should().Be(ActionDependencyType.Optional);

        (await _actions.DeleteActionAsync(action.Id)).Result.Should().BeTrue();
        var executor = new DefaultSurrealExecutor(_connection);
        (await executor.QueryAsync<AZOA.WebAPI.Persistence.SurrealDb.Models.ActionDependency>(
            SurrealQuery<AZOA.WebAPI.Persistence.SurrealDb.Models.ActionDependency>.From())).Should().BeEmpty();
    }
}
