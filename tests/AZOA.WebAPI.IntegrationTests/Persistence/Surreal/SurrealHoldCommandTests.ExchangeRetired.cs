using System.Text.Json;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Retired exchange rows: the operator list and the create-once resolution over real SurrealDB.</summary>
public sealed partial class SurrealHoldCommandTests
{
    [Fact]
    public async Task RetiredExchangeRowsListOnlyOpenExchangesAndResolveOnce()
    {
        var open = Guid.NewGuid();
        await Save(OperationRow(open, "Exchange", OperationLog.StatusKind.PendingConfirmation));
        await Save(OperationRow(Guid.NewGuid(), "Exchange", OperationLog.StatusKind.Completed));
        await Save(OperationRow(Guid.NewGuid(), "Mint", OperationLog.StatusKind.PendingConfirmation));

        (await _store.ListRetiredExchangeRowsAsync(50)).Select(row => row.Id).Should().ContainSingle()
            .Which.Should().EndWith(open.ToString("N"));
        (await _store.GetRetiredExchangeRowAsync(open)).Should().NotBeNull();

        var resolution = new ExchangeRetiredResolution { Id = open.ToString("N"), OperationId = open.ToString("N"), Outcome = "Confirmed",
            TransactionHash = "tx-1", Reason = "Credited by hand.", ResolvedByUserId = _actor.ToString("N"), CreatedAt = DateTimeOffset.UtcNow };
        var results = await Task.WhenAll(_store.RecordRetiredResolutionAsync(resolution), _store.RecordRetiredResolutionAsync(resolution));

        results.Should().OnlyContain(row => row != null && row.Outcome == "Confirmed");
        (await _store.RecordRetiredResolutionAsync(new ExchangeRetiredResolution { Id = resolution.Id, OperationId = resolution.OperationId,
            Outcome = "Failed", TransactionHash = "tx-1", Reason = "Different.", ResolvedByUserId = resolution.ResolvedByUserId,
            CreatedAt = DateTimeOffset.UtcNow })).Should().BeNull();
        (await _store.GetRetiredResolutionAsync(open))!.Outcome.Should().Be("Confirmed");
    }

    private static OperationLog OperationRow(Guid id, string type, OperationLog.StatusKind status) => new()
    {
        Id = id.ToString("N"), OperationType = type, Status = status, CreatedDate = DateTimeOffset.UtcNow,
        Parameters = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["TxHash"] = "tx-1" }),
    };
}
