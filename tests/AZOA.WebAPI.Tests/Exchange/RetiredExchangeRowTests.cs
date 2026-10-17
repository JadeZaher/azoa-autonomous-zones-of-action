// SPDX-License-Identifier: UNLICENSED
// Retired exchange rows close only on an operator resolution that the observed chain outcome agrees with
// (Services/Reserve/AGENTS.md §retired-rows).

using System.Text.Json;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Exchange;

public sealed class RetiredExchangeRowTests
{
    private static readonly Guid OperationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Operator = Guid.Parse("99999999-8888-7777-6666-555555555555");

    [Theory]
    [InlineData("Confirmed", true, false)]
    [InlineData("Failed", false, true)]
    public async Task A_resolution_closes_only_the_chain_outcome_it_attests(string outcome, bool settles, bool compensates)
    {
        var store = new InMemoryResolutions(Row("tx-1"));
        var service = new ExchangeRetiredRowService(store);
        var reconciler = new ExchangeSettlementReconciler(store);
        var operation = Operation("tx-1");

        (await reconciler.SettleConfirmedAsync(operation)).Should().BeFalse("no operator has attested anything yet");
        var resolved = await service.ResolveAsync(OperationId, new() { Outcome = outcome, Reason = "Credited the project by hand." }, Operator);

        resolved.IsError.Should().BeFalse(resolved.Message);
        (await reconciler.SettleConfirmedAsync(operation)).Should().Be(settles);
        (await reconciler.CompensateFailedAsync(operation)).Should().Be(compensates);
        (await reconciler.SettleConfirmedAsync(Operation("tx-other"))).Should().BeFalse("the attestation binds the exact observed transaction");
    }

    [Fact]
    public async Task A_second_different_resolution_is_a_conflict_and_an_identical_one_replays()
    {
        var store = new InMemoryResolutions(Row("tx-1"));
        var service = new ExchangeRetiredRowService(store);
        var request = new RetiredExchangeResolutionRequest { Outcome = "Confirmed", Reason = "Verified nothing was owed." };

        (await service.ResolveAsync(OperationId, request, Operator)).IsError.Should().BeFalse();
        (await service.ResolveAsync(OperationId, request, Operator)).IsError.Should().BeFalse("an identical replay returns the original");
        var changed = await service.ResolveAsync(OperationId, new() { Outcome = "Failed", Reason = "Changed my mind." }, Operator);

        changed.Code.Should().Be(AzoaErrorCodes.Conflict);
        store.Saved.Should().ContainSingle().Which.Outcome.Should().Be("Confirmed");
    }

    [Theory]
    [InlineData("Settled", "reason")]
    [InlineData("Confirmed", "")]
    public async Task An_unrecognised_outcome_or_missing_reason_records_nothing(string outcome, string reason)
    {
        var store = new InMemoryResolutions(Row("tx-1"));

        var result = await new ExchangeRetiredRowService(store).ResolveAsync(OperationId, new() { Outcome = outcome, Reason = reason }, Operator);

        result.Code.Should().Be(AzoaErrorCodes.InvalidRequest);
        store.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task A_row_with_no_observed_transaction_cannot_be_resolved()
    {
        var store = new InMemoryResolutions(Row(null));

        var result = await new ExchangeRetiredRowService(store).ResolveAsync(OperationId,
            new() { Outcome = "Failed", Reason = "Never sent." }, Operator);

        result.Code.Should().Be(AzoaErrorCodes.NotFound);
        store.Saved.Should().BeEmpty();
    }

    private static BlockchainOperation Operation(string txHash) => new()
    {
        Id = OperationId, OperationType = ReserveExchangeManager.ChainOperationType, Status = OperationStatus.PendingConfirmation,
        Parameters = new() { [ExchangeSettlementReconciler.TransactionHashParameter] = txHash },
    };

    private static OperationLog Row(string? txHash) => new()
    {
        Id = OperationId.ToString("N"), OperationType = ReserveExchangeManager.ChainOperationType,
        Status = OperationLog.StatusKind.PendingConfirmation,
        Parameters = JsonSerializer.SerializeToElement(txHash is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [ExchangeSettlementReconciler.TransactionHashParameter] = txHash }),
    };

    /// <summary>Create-once resolution store with the same replay/conflict contract as the Surreal store.</summary>
    private sealed class InMemoryResolutions(OperationLog row) : IExchangeRetiredResolutionStore
    {
        public List<ExchangeRetiredResolution> Saved { get; } = [];

        public Task<ExchangeRetiredResolution?> GetRetiredResolutionAsync(Guid operationId, CancellationToken ct = default)
            => Task.FromResult(Saved.FirstOrDefault(saved => saved.Id == operationId.ToString("N")));

        public Task<ExchangeRetiredResolution?> RecordRetiredResolutionAsync(ExchangeRetiredResolution resolution, CancellationToken ct = default)
        {
            var stored = Saved.FirstOrDefault(saved => saved.Id == resolution.Id);
            if (stored is null) Saved.Add(stored = resolution);
            return Task.FromResult(stored.Outcome == resolution.Outcome && stored.TransactionHash == resolution.TransactionHash
                && stored.Reason == resolution.Reason ? stored : null);
        }

        public Task<IReadOnlyList<OperationLog>> ListRetiredExchangeRowsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OperationLog>>([row]);
    }
}
