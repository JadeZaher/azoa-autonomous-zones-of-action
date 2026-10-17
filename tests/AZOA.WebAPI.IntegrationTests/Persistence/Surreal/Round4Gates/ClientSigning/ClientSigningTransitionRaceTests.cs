using AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Interleaving;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.ClientSigning;

/// <summary>D-3 submit-signed: the operation-row status CAS lets exactly one transition leave AwaitingSignature (two submits, or submit vs the stuck-row reconcile).</summary>
public sealed class ClientSigningTransitionRaceTests : IAsyncLifetime
{
    private InterleavingDatabase _database = null!;
    private SurrealOperationLogClientSigningStore _store = null!;

    public async Task InitializeAsync()
    {
        _database = await InterleavingDatabase.CreateAsync("operation_log");
        _store = new SurrealOperationLogClientSigningStore(_database.Executor);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Two_concurrent_submits_of_one_awaiting_operation_have_one_winner()
    {
        var operation = await AwaitingAsync();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => _store.TryTransitionAsync(operation,
            "AwaitingSignature", "PendingConfirmation", new Dictionary<string, string> { ["submission"] = index.ToString() }, null)));

        outcomes.Count(won => won).Should().Be(1, "a second broadcast of the client-signed bytes must not be admitted");
        var stored = await StoredAsync(operation);
        stored.Status.Should().Be(OperationLog.StatusKind.PendingConfirmation);
        stored.Parameters!.Value.GetProperty("submission").GetString().Should().Be(outcomes[0] ? "0" : "1", "the row carries the winner's submission only");
    }

    [Fact]
    public async Task Submit_racing_the_stuck_row_reconcile_leaves_one_terminal_story()
    {
        var operation = await AwaitingAsync();
        var submit = _store.TryTransitionAsync(operation, "AwaitingSignature", "PendingConfirmation",
            new Dictionary<string, string> { ["by"] = "submit" }, null);
        var expire = _store.TryTransitionAsync(operation, "AwaitingSignature", "Failed",
            new Dictionary<string, string> { ["by"] = "reconcile" }, DateTimeOffset.UtcNow);
        var outcomes = await Task.WhenAll(submit, expire);

        outcomes.Count(won => won).Should().Be(1);
        var stored = await StoredAsync(operation);
        stored.Status.Should().Be(outcomes[0] ? OperationLog.StatusKind.PendingConfirmation : OperationLog.StatusKind.Failed);
        stored.Parameters!.Value.GetProperty("by").GetString().Should().Be(outcomes[0] ? "submit" : "reconcile");
        (stored.CompletedDate is null).Should().Be(outcomes[0], "only the terminal reconcile stamps completion");
        (await _store.ListStuckAsync(DateTimeOffset.UtcNow.AddHours(1), 10)).Should().NotContain(operation, "neither outcome leaves it awaiting");
    }

    private async Task<Guid> AwaitingAsync()
    {
        var id = Guid.NewGuid();
        var row = ModelValidRow.Fill(new OperationLog());
        row.Id = SurrealId.ToSurrealId(id);
        row.Status = OperationLog.StatusKind.AwaitingSignature;
        row.CreatedDate = DateTimeOffset.UtcNow.AddMinutes(-5);
        (await _database.Executor.ExecuteAsync(SurrealWriter.Create(row))).EnsureAllOk();
        return id;
    }

    private async Task<OperationLog> StoredAsync(Guid id)
        => (await _database.Executor.QuerySingleAsync<OperationLog>(SurrealQuery<OperationLog>.Key(SurrealId.ToSurrealId(id))))!;
}
