using System.Threading.Tasks;
using FluentAssertions;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Connection;
using Oasis.SurrealDb.Client.Tests; // FakeHttpHandler

namespace Oasis.SurrealDb.Client.Tests.Transaction;

/// <summary>
/// Regression tests for HIGH#1 — when the COMMIT round-trip itself faults,
/// the dispose path must still send CANCEL TRANSACTION and IsCommitted must
/// stay <c>false</c>. The earlier implementation set the committed bit BEFORE
/// the await, leaking the server-side transaction whenever COMMIT failed.
/// </summary>
public class SurrealTransactionFailedCommitTests
{
    private static SurrealConnectionOptions Opts() => new()
    {
        Endpoint   = "http://localhost:8442",
        MaxRetries = 1,
    };

    private const string OkBody  = """[ { "status": "OK",  "time": "1µs", "result": null } ]""";
    private const string ErrBody = """[ { "status": "ERR", "time": "1µs", "result": null, "detail": "synthetic commit failure" } ]""";

    [Fact]
    public async Task CommitAsync_ServerReturnsErr_PreservesIsCommittedFalse_AndDisposeSendsCancel()
    {
        var handler = new FakeHttpHandler();
        handler.EnqueueOk(OkBody);  // BEGIN
        handler.EnqueueOk(OkBody);  // user statement
        handler.EnqueueOk(ErrBody); // COMMIT — server-side error
        handler.EnqueueOk(OkBody);  // CANCEL on dispose

        await using var conn = new HttpSurrealConnection(handler, Opts());

        var txn = await conn.BeginTransactionAsync();
        await conn.ExecuteRawAsync("UPDATE wallet:abc SET amount = 100;");

        var commit = async () => await txn.CommitAsync();
        await commit.Should().ThrowAsync<SurrealStatementException>(
            "the COMMIT result has status ERR; the failure must bubble to the caller");

        txn.IsCommitted.Should().BeFalse(
            "commit failed server-side so IsCommitted must NOT flip to true");

        await txn.DisposeAsync();

        handler.Requests.Should().HaveCount(4);
        handler.Requests[0].Body.Should().Be("BEGIN TRANSACTION;");
        handler.Requests[1].Body.Should().StartWith("UPDATE wallet:abc");
        handler.Requests[2].Body.Should().Be("COMMIT TRANSACTION;");
        handler.Requests[3].Body.Should().Be("CANCEL TRANSACTION;",
            "dispose-after-failed-commit must issue CANCEL to release the server-side txn");
    }
}
