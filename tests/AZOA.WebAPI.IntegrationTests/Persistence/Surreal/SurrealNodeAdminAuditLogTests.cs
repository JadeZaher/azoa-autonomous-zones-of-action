using AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Interleaving;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Real audit transactions preserve the head and chained rows across contention and duplicate failure.</summary>
public sealed class SurrealNodeAdminAuditLogTests : IAsyncLifetime
{
    private InterleavingDatabase _database = null!;
    private NodeAdminAuditLog _audit = null!;

    public async Task InitializeAsync()
    {
        _database = await InterleavingDatabase.CreateAsync("node_admin_audit", "node_admin_audit_head");
        _audit = new(_database.Executor, NullLogger<NodeAdminAuditLog>.Instance);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task ConcurrentAppendRetriesRefusedHeadCasWithoutForkOrGap()
    {
        var original = await _audit.AppendAsync(Entry("original"), CancellationToken.None);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstExecutionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var executions = 0;
        var refused = 0;
        var intercepted = new Mock<ISurrealExecutor>(MockBehavior.Strict);
        intercepted.Setup(executor => executor.QuerySingleAsync<NodeAdminAuditHead>(It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async (SurrealQuery query, CancellationToken ct) =>
            {
                var head = await _database.Executor.QuerySingleAsync<NodeAdminAuditHead>(query, ct);
                var arrival = Interlocked.Increment(ref reads);
                if (arrival <= 2)
                {
                    head!.Seq.Should().Be(original.Seq);
                    head.Hash.Should().Be(original.Hash);
                    if (arrival == 2) ready.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
                return head;
            });
        intercepted.Setup(executor => executor.ExecuteAsync(It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async (SurrealQuery query, CancellationToken ct) =>
            {
                var execution = Interlocked.Increment(ref executions);
                if (execution == 2) await firstExecutionCompleted.Task.WaitAsync(ct);
                try
                {
                    var response = await _database.Executor.ExecuteAsync(query, ct);
                    try { response.EnsureAllOk(); }
                    catch (SurrealStatementException exception) when (exception.Message.Contains("NODE_ADMIN_AUDIT_HEAD_MOVED", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref refused);
                        throw;
                    }
                    return response;
                }
                finally { if (execution == 1) firstExecutionCompleted.TrySetResult(); }
            });
        var contended = new NodeAdminAuditLog(intercepted.Object, NullLogger<NodeAdminAuditLog>.Instance);
        var first = contended.AppendAsync(Entry("first"), CancellationToken.None);
        var second = contended.AppendAsync(Entry("second"), CancellationToken.None);
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { release.TrySetResult(); }
        var appended = await Task.WhenAll(first, second);

        refused.Should().BeGreaterThan(0, "both contenders captured the same head, so one must lose its conditional advance");
        reads.Should().BeGreaterThan(2, "the losing appender must read the new head before retrying");
        appended.Select(row => row.Seq).Should().BeEquivalentTo(new long[] { 2, 3 });
        var rows = await _audit.ListAsync(0, 10, CancellationToken.None);
        rows.Select(row => row.Seq).Should().Equal(1, 2, 3);
        rows.Select(row => row.RequestId).Should().BeEquivalentTo(new[] { "original", "first", "second" });
        rows[0].PrevHash.Should().Be(NodeAdminAuditLog.GenesisHash);
        rows[1].PrevHash.Should().Be(rows[0].Hash);
        rows[2].PrevHash.Should().Be(rows[1].Hash);
        var headAfter = (await _audit.GetHeadAsync(CancellationToken.None))!;
        headAfter.Seq.Should().Be(3);
        headAfter.Hash.Should().Be(rows[2].Hash);
        var verified = await _audit.VerifyAsync(1, CancellationToken.None);
        verified.Ok.Should().BeTrue();
        verified.VerifiedThroughSeq.Should().Be(3);
        verified.HeadHash.Should().Be(headAfter.Hash);
    }

    [Fact]
    public async Task DuplicateNextRowRollsBackEveryHeadAdvanceAndPreservesExistingEvidence()
    {
        var original = await _audit.AppendAsync(Entry("original"), CancellationToken.None);
        var occupied = new NodeAdminAudit
        {
            Id = NodeAdminAuditLog.RecordIdFor(2), Seq = 2,
            At = NodeAdminCrypto.FormatTimestamp(DateTimeOffset.UtcNow), Actor = "system",
            Action = "fixture.occupied", Outcome = NodeAdminAuditLog.OutcomeRead,
            RequestId = "retained-evidence", PrevHash = original.Hash,
        };
        occupied.Hash = NodeAdminAuditLog.ComputeHash(occupied.PrevHash, occupied.Seq, occupied.At,
            occupied.Actor, occupied.Action, occupied.Target, occupied.Outcome, occupied.Detail);
        (await _database.Executor.ExecuteAsync(SurrealWriter.Create(occupied))).EnsureAllOk();

        Func<Task> append = () => _audit.AppendAsync(Entry("must-not-land"), CancellationToken.None);
        await append.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The node-admin audit chain could not be extended after repeated head contention.");

        var head = (await _audit.GetHeadAsync(CancellationToken.None))!;
        head.Seq.Should().Be(original.Seq);
        head.Hash.Should().Be(original.Hash);
        var retained = await _database.Executor.QuerySingleAsync<NodeAdminAudit>(SurrealQuery<NodeAdminAudit>.Key(occupied.Id));
        retained.Should().BeEquivalentTo(occupied);
        var rows = await _audit.ListAsync(0, 10, CancellationToken.None);
        rows.Should().HaveCount(2);
        rows.Select(row => row.RequestId).Should().BeEquivalentTo(new[] { "original", "retained-evidence" });
    }

    private static NodeAdminAuditEntry Entry(string requestId)
        => new("system", [], "fixture.append", "", NodeAdminAuditLog.OutcomeRead, "", "", requestId);
}
