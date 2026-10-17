using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// <see cref="KycEvidenceFence"/> on SurrealDB 3.x (round-4 L2 acceptance): an empty scope list is a
/// no-op that still commits its transaction, and several scopes bump one guard row each, so every
/// consumer of any of those subjects conflicts with the evidence write.
/// </summary>
public sealed class KycEvidenceFenceTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "kyc_authority_guard");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
    }

    public async Task DisposeAsync() { _connection.Dispose(); await SurrealTestSchema.DropAsync(_namespace); }

    [Fact]
    public async Task An_empty_scope_list_commits_and_touches_no_guard()
    {
        var response = await KycEvidenceFence.ExecuteAsync(_executor,
            KycEvidenceFence.Mutation(Array.Empty<string>(), null, SurrealQuery.Of("RETURN 'committed'")), CancellationToken.None);

        response.GetValues<string>(2).Should().Equal("committed");
        (await GuardsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Several_subjects_each_advance_their_own_guard_once_per_write()
    {
        var first = "user:" + Guid.NewGuid().ToString("N");
        var second = "user:" + Guid.NewGuid().ToString("N");
        var tenant = "user:" + Guid.NewGuid().ToString("N");

        for (var write = 0; write < 2; write++)
            await KycEvidenceFence.ExecuteAsync(_executor,
                KycEvidenceFence.Mutation([first, second, first], tenant, SurrealQuery.Of("RETURN NONE")), CancellationToken.None);

        var guards = await GuardsAsync();
        guards.Select(guard => guard.Id).Should().BeEquivalentTo(
            [KycEvidenceFence.GuardId(first, tenant), KycEvidenceFence.GuardId(second, tenant)],
            "a repeated subject is one guard, and the tenant scopes the guard id");
        guards.Should().OnlyContain(guard => guard.AuthorityRevision == 2);
    }

    private async Task<IReadOnlyList<KycAuthorityGuard>> GuardsAsync()
        => (await _executor.QueryAsync<KycAuthorityGuard>(SurrealQuery<KycAuthorityGuard>.From())).ToArray();
}
