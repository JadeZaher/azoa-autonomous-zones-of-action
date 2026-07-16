using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealNodeTransparencyStoreTests : IAsyncLifetime
{
    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealNodeTransparencyStore _transparency = null!;
    private SurrealNodeFeeScheduleStore _fees = null!;
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
        var executor = new DefaultSurrealExecutor(_connection);
        _transparency = new SurrealNodeTransparencyStore(executor);
        _fees = new SurrealNodeFeeScheduleStore(executor);
        await SurrealTestSchema.BootstrapAsync(
            _testNamespace,
            NodeFeeSchedule.SchemaNameConst,
            NodeFeeAudit.SchemaNameConst);
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
    public async Task CompositeCursor_WithIdenticalTimestamps_HasNoDuplicatesOrSkips()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var occurredAt = DateTimeOffset.Parse("2026-07-11T12:00:00Z");
        var actor = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(Guid.NewGuid()))!;
        var ids = new[]
        {
            new string('1', 32),
            new string('2', 32),
            new string('3', 32),
        };
        for (var index = 0; index < ids.Length; index++)
        {
            var version = index + 1L;
            var schedule = new NodeFeeSchedule
            {
                Id = NodeFeeSchedule.LocalId,
                Version = version,
                UpdatedByAvatarId = actor,
                CreatedAt = occurredAt,
                UpdatedAt = occurredAt,
            };
            var audit = new NodeFeeAudit
            {
                Id = ids[index],
                Action = "ScheduleUpdated",
                ActorAvatarId = actor,
                PreviousVersion = version - 1,
                NewVersion = version,
                ScheduleJson = $"{{\"version\":{version}}}",
                OccurredAt = occurredAt,
            };
            var saved = await _fees.UpdateScheduleWithAuditAsync(
                schedule,
                audit,
                index == 0 ? null : version - 1);
            saved.IsError.Should().BeFalse(saved.Message);
        }

        var seen = new List<string>();
        NodeTransparencyStoreCursor? cursor = null;
        for (var pageNumber = 0; pageNumber < ids.Length; pageNumber++)
        {
            var page = await _transparency.ListFeeAuditAsync(1, cursor);
            page.IsError.Should().BeFalse(page.Message);
            var row = page.Result.Should().ContainSingle().Which;
            seen.Add(SurrealRecordGuid.BareId(row.Id));
            cursor = new NodeTransparencyStoreCursor(row.OccurredAt, row.Id);
        }

        seen.Should().Equal(ids.Reverse());
        seen.Should().OnlyHaveUniqueItems();
        var exhausted = await _transparency.ListFeeAuditAsync(1, cursor);
        exhausted.Result.Should().BeEmpty();
    }

    private static async Task<bool> ProbeSurrealAsync()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await probe.GetAsync($"{SurrealTestDefaults.Endpoint}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
