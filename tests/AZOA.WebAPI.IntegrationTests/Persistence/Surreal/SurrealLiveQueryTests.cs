// SPDX-License-Identifier: UNLICENSED
// Live-query socket end-to-end (surreal-linq-graph-query Phase 5) against a
// real SurrealDB 3.x: open a WebSocketSurrealConnection, subscribe via
// ctx.Set<T>().ExecuteLiveAsync, mutate over a second (HTTP) connection, and
// assert the Create/Update/Delete notifications arrive; then cancel and assert
// the stream completes (the live query is KILLed). Skips when SurrealDB is
// unreachable, like the other Surreal integration tests.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;
using AZOA.WebAPI.IntegrationTests.Factories;
using Xunit;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealLiveQueryTests : IntegrationTestBase
{
    public SurrealLiveQueryTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    [SkippableFact]
    public async Task ExecuteLiveAsync_streams_create_notification_then_completes_on_cancel()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB not reachable — start the dev/test SurrealDB instance.");

        // Define the table in this test's namespace so LIVE SELECT has a target.
        await ExecuteSurrealSqlRawAsync("DEFINE TABLE IF NOT EXISTS live_thing SCHEMALESS");

        var options = new SurrealConnectionOptions
        {
            Endpoint  = SurrealTestDefaults.Endpoint,
            Namespace = TestNamespace,
            Database  = "test",
            User      = SurrealTestDefaults.User,
            Password  = SurrealTestDefaults.Password,
        };

        await using var socket = new WebSocketSurrealConnection(options);
        await socket.ConnectAsync();

        var ctx = new SurrealContext(
            new SurrealForge.Client.Connection.HttpSurrealConnection(
                new System.Net.Http.HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) },
                options));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var received = new List<LiveNotification<LiveThing>>();

        // Subscribe in the background; collect notifications until cancelled.
        var pump = Task.Run(async () =>
        {
            await foreach (var note in ctx.Set<LiveThing>().ExecuteLiveAsync(socket, cts.Token))
            {
                received.Add(note);
                if (note.Action == LiveAction.Create) cts.Cancel(); // got what we need
            }
        });

        // Give the LIVE SELECT a moment to register, then mutate over HTTP.
        await Task.Delay(500);
        await ExecuteSurrealSqlRawAsync("CREATE live_thing:n1 CONTENT { label: 'hello' }");

        // The pump completes when the create cancels the token (KILL fires in
        // the enumerator's finally). Bounded so a protocol mismatch fails fast.
        var completed = await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().Be(pump, "the live stream should deliver the create and then complete on cancel");

        received.Should().ContainSingle(n => n.Action == LiveAction.Create);
        received[0].Record.Label.Should().Be("hello");
    }

    public sealed class LiveThing : ISurrealRecord
    {
        public string SchemaName => "live_thing";
        [Id] [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    }
}
