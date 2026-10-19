using AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Interleaving;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Real-DB atomic rollback, directory CAS and duplicate replay for node-admin commits.</summary>
public sealed class SurrealNodeAdminStoreTests : IAsyncLifetime
{
    private InterleavingDatabase _database = null!;
    private SurrealNodeAdminStore _store = null!;

    public async Task InitializeAsync()
    {
        _database = await InterleavingDatabase.CreateAsync("node_admin_directory", "node_admin_role");
        _store = new(_database.Executor);
        (await _database.Executor.ExecuteAsync(SurrealWriter.Create(new NodeAdminDirectory()))).EnsureAllOk();
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task RefusedMutationRollsBackDirectoryAndEarlierRoleWrite()
    {
        var role = Role("original");
        (await _database.Executor.ExecuteAsync(SurrealWriter.Create(role))).EnsureAllOk();
        var update = SurrealWriter.UpdateOnly<NodeAdminRole>(role.Id)
            .Where(row => row.Revision == 1).Set(row => row.Revision, 2L).Set(row => row.Name, "changed").Build();
        var refused = SurrealWriter.UpdateOnly<NodeAdminRole>(role.Id)
            .Where(row => row.Revision == 99).Set(row => row.Name, "must-not-land").Build();

        (await _store.CommitAsync(0, [update, refused], default)).Should().Be(NodeAdminCommit.Conflict);

        (await _store.GetDirectoryAsync(default))!.Revision.Should().Be(0);
        var unchanged = (await _store.GetRoleAsync(role.Id, default))!;
        unchanged.Name.Should().Be(role.Name);
        unchanged.Revision.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentDirectoryClaimsCommitOneRoleAndDuplicateCannotOverwriteIt()
    {
        var candidates = new[] { Role("first"), Role("second") };
        var outcomes = await Task.WhenAll(candidates.Select(role =>
            _store.CommitAsync(0, [SurrealWriter.Create(role)], default)));

        outcomes.Count(outcome => outcome == NodeAdminCommit.Committed).Should().Be(1);
        outcomes.Count(outcome => outcome == NodeAdminCommit.Conflict).Should().Be(1);
        (await _store.GetDirectoryAsync(default))!.Revision.Should().Be(1);
        var saved = (await _store.ListRolesAsync(default)).Should().ContainSingle().Subject;
        var winner = candidates[Array.IndexOf(outcomes, NodeAdminCommit.Committed)];
        saved.Id.Should().Be(winner.Id);
        saved.Name.Should().Be(winner.Name);

        var replay = Role("replacement");
        replay.Id = winner.Id;
        (await _store.CommitAsync(null, [SurrealWriter.Create(replay)], default)).Should().Be(NodeAdminCommit.Duplicate);
        (await _store.GetRoleAsync(winner.Id, default))!.Name.Should().Be(winner.Name);
        (await _store.ListRolesAsync(default)).Should().ContainSingle();
        (await _store.GetDirectoryAsync(default))!.Revision.Should().Be(1);
    }

    private static NodeAdminRole Role(string name) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Name = name, Revision = 1,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };
}
