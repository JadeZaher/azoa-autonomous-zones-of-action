using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Interleaving;

public sealed class FiatPayoutLegacyFenceGuardTests : IAsyncLifetime
{
    private InterleavingDatabase _database = null!;

    public async Task InitializeAsync()
        => _database = await InterleavingDatabase.CreateAsync("fiat_payout", "fiat_payout_request_fence");

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Legacy_admission_after_void_preread_throws_and_rolls_back_the_absence_fence()
    {
        var subject = Guid.NewGuid();
        const string key = "legacy-between-read-and-batch";
        var now = DateTimeOffset.UtcNow;
        var payout = new FiatPayout
        {
            Id = FiatPayout.RecordIdFor(subject, key), SubjectUserId = SurrealLink.ToLink("user", subject.ToString("N"))!,
            TenantId = SurrealLink.ToLink("user", Guid.NewGuid().ToString("N")), RequestKey = key,
            ChainType = "Algorand", AssetId = "1", QuantityBaseUnits = "50000", AmountMinorUnits = 500,
            CurrencyCode = "USD", UnitsPerMinorUnit = "100", Status = FiatPayoutStatuses.Requested,
            Environment = "test", ChainNetwork = "Devnet", RequestedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        var fence = new FiatPayoutRequestFence
        {
            Id = payout.Id, SubjectUserId = payout.SubjectUserId, TenantId = payout.TenantId,
            RequestKey = key, Environment = payout.Environment, ChainNetwork = payout.ChainNetwork,
            Verdict = "voided", CreatedAt = now,
        };
        var executor = new InsertLegacyBeforeBatchExecutor(_database.Executor, async () =>
            (await _database.Executor.ExecuteAsync(SurrealWriter.Create(payout))).EnsureAllOk());
        var result = await new SurrealFiatPayoutLedger(executor).VoidAsync(fence);

        executor.Injections.Should().Be(1, "the payout appears after VoidAsync's missing-row preread, before its real transaction");
        executor.ActualDatabaseFailure.Should().Contain("PAYOUT_ALREADY_ADMITTED");
        result.IsError.Should().BeFalse();
        result.Result!.Voided.Should().BeFalse();
        result.Result.Row!.Id.Should().Be(payout.Id);
        result.Result.Row.Status.Should().Be(FiatPayoutStatuses.Requested);
        (await _database.Executor.QuerySingleAsync<FiatPayoutRequestFence>(SurrealQuery<FiatPayoutRequestFence>.Key(payout.Id)))
            .Should().BeNull("the THROW rolls back the preceding void-fence CREATE");
        var saved = (await _database.Executor.QuerySingleAsync<FiatPayout>(SurrealQuery<FiatPayout>.Key(payout.Id)))!;
        saved.Version.Should().Be(0);
        saved.Status.Should().Be(FiatPayoutStatuses.Requested);
        saved.AmountMinorUnits.Should().Be(payout.AmountMinorUnits);
        var replay = await new SurrealFiatPayoutLedger(_database.Executor).ClaimAsync(payout);
        replay.IsError.Should().BeFalse();
        replay.Result!.Created.Should().BeFalse();
        (await _database.Executor.QueryAsync<FiatPayout>(SurrealQuery<FiatPayout>.From())).Should().ContainSingle();
    }

    private sealed class InsertLegacyBeforeBatchExecutor(ISurrealExecutor inner, Func<Task> insert) : ISurrealExecutor
    {
        public int Injections { get; private set; }
        public string? ActualDatabaseFailure { get; private set; }
        public Task<IReadOnlyList<T>> QueryAsync<T>(SurrealQuery query, CancellationToken ct = default)
            => inner.QueryAsync<T>(query, ct);
        public Task<T?> QuerySingleAsync<T>(SurrealQuery query, CancellationToken ct = default) where T : class
            => inner.QuerySingleAsync<T>(query, ct);
        public async Task<SurrealResponse> ExecuteAsync(SurrealQuery query, CancellationToken ct = default)
        {
            if (Injections == 0) { Injections++; await insert(); }
            var response = await inner.ExecuteAsync(query, ct);
            try { response.EnsureAllOk(); }
            catch (SurrealStatementException exception)
            {
                ActualDatabaseFailure = exception.Message;
                throw;
            }
            return response;
        }
    }
}
