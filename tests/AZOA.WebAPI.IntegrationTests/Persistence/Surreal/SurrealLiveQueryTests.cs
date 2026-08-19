// SPDX-License-Identifier: UNLICENSED
// Live-query end-to-end (surreal-linq-graph-query Phase 5) against a real
// SurrealDB 3.x: open a SurrealLiveClient over a ws:// SDK connection, subscribe
// via ctx.Set<T>().ExecuteLiveAsync, mutate over a second (HTTP) connection, and
// assert the Create notification arrives; then cancel and assert the stream
// completes (the live query is KILLed when the enumerable disposes the
// subscription). Skips when SurrealDB is unreachable, like the other Surreal
// integration tests.
//
// SurrealForge 1.0.0 deleted the legacy WebSocketSurrealConnection transport;
// live queries now ride the SDK client, which must point at ws(s)://…/rpc —
// every SDK live method throws NotSupportedException over http://.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Live;
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

        // The subscription needs a stateful transport, so it rides its own
        // ws:// SDK connection; the writes below go over HTTP, which is what
        // makes an arriving notification proof that it came off the socket.
        var socketOptions = new SurrealConnectionOptions
        {
            Endpoint  = WebSocketEndpoint(SurrealTestDefaults.Endpoint),
            Namespace = TestNamespace,
            Database  = "test",
            User      = SurrealTestDefaults.User,
            Password  = SurrealTestDefaults.Password,
        };

        await using var socket = new SurrealDbNetConnection(socketOptions);
        var live = new SurrealLiveClient(socket.Client);

        // SDK/CBOR connection (SurrealForge 1.0.0) for the query surface.
        await using var queryConnection = new SurrealDbNetConnection(options);
        var ctx = new SurrealContext(queryConnection);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var received = new List<LiveNotification<LiveThing>>();

        // Subscribe in the background; collect notifications until cancelled.
        var pump = Task.Run(async () =>
        {
            await foreach (var note in ctx.Set<LiveThing>().ExecuteLiveAsync(live, cts.Token))
            {
                received.Add(note);
                if (note.Action == LiveAction.Create) cts.Cancel(); // got what we need
            }
        });

        // Give the LIVE SELECT a moment to register, then mutate over HTTP.
        await Task.Delay(500);
        await ExecuteSurrealSqlRawAsync("CREATE live_thing:n1 CONTENT { label: 'hello' }");

        // The pump completes when the create cancels the token (the enumerable
        // disposes the subscription, which KILLs the server-side live query).
        // Bounded so a protocol mismatch fails fast.
        var completed = await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().Be(pump, "the live stream should deliver the create and then complete on cancel");
        try { await pump; } catch (OperationCanceledException) { /* the cancel above */ }

        received.Should().ContainSingle(n => n.Action == LiveAction.Create);
        received[0].Record.Label.Should().Be("hello");
    }

    /// <summary>The RPC endpoint for the WebSocket transport: same host/port, <c>/rpc</c> path.</summary>
    private static string WebSocketEndpoint(string httpEndpoint)
    {
        var uri    = new Uri(httpEndpoint);
        var scheme = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return $"{scheme}://{uri.Host}:{uri.Port}/rpc";
    }

    public sealed class LiveThing : ISurrealRecord
    {
        public string SchemaName => "live_thing";
        [Id] [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    }
}
