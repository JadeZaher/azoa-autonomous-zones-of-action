using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.NodeAdmin;

/// <summary>Audit verification remains stable when a valid append commits after its head read.</summary>
public sealed class NodeAdminAuditSnapshotTests(NodeAdminWebApplicationFactory factory)
    : NodeAdminHttpTestBase(factory), IClassFixture<NodeAdminWebApplicationFactory>
{
    [Fact]
    public async Task Verification_reads_only_through_the_captured_head_even_when_an_append_commits_before_paging()
    {
        await RootLoginAsync();
        using var scope = Factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ISurrealExecutor>();
        var audit = scope.ServiceProvider.GetRequiredService<NodeAdminAuditLog>();
        var intercepted = new Mock<ISurrealExecutor>(MockBehavior.Strict);
        NodeAdminAuditHead? captured = null;
        intercepted.Setup(candidate => candidate.QuerySingleAsync<NodeAdminAuditHead>(It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async (SurrealQuery query, CancellationToken ct) =>
            {
                captured = await executor.QuerySingleAsync<NodeAdminAuditHead>(query, ct);
                await audit.AppendAsync(new NodeAdminAuditEntry("system", [], "snapshot.append", "", NodeAdminAuditLog.OutcomeRead, "", "", "snapshot-test"), ct);
                return captured;
            });
        intercepted.Setup(candidate => candidate.QueryAsync<NodeAdminAudit>(It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .Returns((SurrealQuery query, CancellationToken ct) => executor.QueryAsync<NodeAdminAudit>(query, ct));
        var verifier = new NodeAdminAuditLog(intercepted.Object, NullLogger<NodeAdminAuditLog>.Instance);

        var verified = await verifier.VerifyAsync(1, CancellationToken.None);

        verified.Ok.Should().BeTrue();
        verified.VerifiedThroughSeq.Should().Be(captured!.Seq);
        verified.HeadHash.Should().Be(captured.Hash);
        (await audit.GetHeadAsync(CancellationToken.None))!.Seq.Should().BeGreaterThan(captured.Seq);
    }
}
