// SPDX-License-Identifier: UNLICENSED
// W5.1 — the sponsorship moment end to end: what refuses before anything moves, what is handed
// back when settlement does not happen, and what a repeated request does.

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Exchange;

public class ReserveExchangeManagerTests
{
    // ── The happy path, and where the money actually went ──────────────────────

    [Fact]
    public async Task A_buy_credits_the_project_treasury_and_never_the_platform_reserve()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller());

        result.IsError.Should().BeFalse(result.Message);
        harness.Movements.Should().ContainSingle()
            .Which.Kind.Should().Be(ProjectTreasuryMovementKind.Credit);
        harness.Submitted().Legs
            .Single(leg => leg.Role == ExchangeLegRole.Funds).ToAddress
            .Should().Be(ExchangeTestHarness.TreasuryAddress);
    }

    [Fact]
    public async Task A_buy_draws_the_units_from_the_named_partition_before_anything_settles()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();

        await manager.ExchangeAsync(harness.Request(), harness.Caller());

        harness.Draws.Should().ContainSingle();
        harness.Draws[0].Kind.Should().Be(ReserveDrawKind.Issue);
        harness.Draws[0].Amount.Should().Be(new BigInteger(250));
    }

    [Fact]
    public async Task The_receipt_carries_the_rate_its_consumer_declared()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller());

        result.Result!.Rate.Should().Be("0.02");
        result.Result.RateSource.Should().Be("ardanova:treasury-rate");
        result.Result.RateVersion.Should().Be("3");
        result.Result.State.Should().Be(ExchangeReceiptState.Completed);
        harness.DeclaredRates.Recorded.Should().ContainSingle()
            .Which.AssetId.Should().Be(ExchangeTestHarness.ProjectAsset);
    }

    [Fact]
    public async Task The_durable_record_is_an_Exchange_signed_under_the_scope_that_pairs_with_it()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();

        await manager.ExchangeAsync(harness.Request(), harness.Caller());

        var operation = harness.Operations.All.Should().ContainSingle().Which;
        operation.OperationType.Should().Be(ReserveExchangeManager.ChainOperationType);
        operation.Status.Should().Be(OperationStatus.Exchanged);
        AzoaScopes.IsScopeValidForOperation(operation.OperationType, operation.SigningScope)
            .Should().BeTrue("the edge consent probe and the custody seam must ask about one scope");
        operation.Parameters["SourceTokenId"].Should().Be(ExchangeTestHarness.PlatformAsset);
        operation.Parameters["Amount"].Should().Be("500");
        operation.Parameters["TargetTokenId"].Should().Be(ExchangeTestHarness.ProjectAsset);
    }

    // ── A hold window ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_hold_window_commits_the_units_to_a_hold_instead_of_delivering_them()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();
        var window = DateTimeOffset.UtcNow.AddDays(30);

        var result = await manager.ExchangeAsync(
            harness.Request(releaseNotBefore: window), harness.Caller());

        result.IsError.Should().BeFalse(result.Message);
        harness.HoldCalls.Should().ContainSingle().Which.Verb.Should().Be("open");
        harness.Draws.Should().BeEmpty("the hold primitive draws the partition itself; drawing twice would double-count");
        result.Result!.HoldReleasesAt.Should().Be(window);
        harness.Submitted().Legs.Should().ContainSingle()
            .Which.Role.Should().Be(ExchangeLegRole.Funds);
    }

    [Fact]
    public async Task A_hold_window_longer_than_the_node_allows_is_refused()
    {
        var harness = new ExchangeTestHarness();
        harness.Options.MaxHoldWindow = TimeSpan.FromDays(30);
        var manager = harness.Build();

        var result = await manager.ExchangeAsync(
            harness.Request(releaseNotBefore: DateTimeOffset.UtcNow.AddDays(90)), harness.Caller());

        result.IsError.Should().BeTrue();
        harness.Idempotency.RecordCount.Should().Be(0, "a refusal before the claim consumes no key");
    }

    [Fact]
    public async Task A_hold_window_in_the_past_is_refused()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();

        var result = await manager.ExchangeAsync(
            harness.Request(releaseNotBefore: DateTimeOffset.UtcNow.AddMinutes(-1)), harness.Caller());

        result.IsError.Should().BeTrue();
    }

    // ── Refusals that must happen BEFORE value moves ──────────────────────────

    [Fact]
    public async Task An_oversubscribed_partition_stops_the_exchange_before_it_settles()
    {
        var harness = new ExchangeTestHarness();
        harness.OversubscribeThePartition();
        var manager = harness.Build();

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller());

        result.IsError.Should().BeTrue();
        harness.Submitter.Submitted.Should().BeEmpty("nothing may settle once the partition refuses");
        harness.Movements.Should().BeEmpty("the project is credited only for funds that actually arrived");
        result.Message.Should().NotContain(
            "Partition", "reserve arithmetic is operator detail and must not reach a consumer");
    }

    [Fact]
    public async Task Identity_readiness_is_checked_before_any_commitment()
    {
        var harness = new ExchangeTestHarness();
        harness.ValueAccess
            .Setup(access => access.RequireValueAccessAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Failure("KYC_FORBIDDEN: identity approval is not current."));
        var manager = harness.Build();

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller());

        result.IsError.Should().BeTrue();
        harness.Draws.Should().BeEmpty();
        harness.Submitter.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_person_with_no_account_on_the_rail_is_refused_and_none_is_provisioned()
    {
        var harness = new ExchangeTestHarness();
        harness.Wallets
            .Setup(store => store.GetByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<AZOA.WebAPI.Interfaces.IWallet>>.Success(
                Array.Empty<AZOA.WebAPI.Interfaces.IWallet>()));
        var manager = harness.Build();

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller());

        result.IsError.Should().BeTrue();
        harness.Submitter.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_closed_project_treasury_refuses_further_funding()
    {
        var harness = new ExchangeTestHarness();
        harness.Treasury
            .Setup(treasury => treasury.ResolveAsync(
                It.IsAny<ProjectTreasuryReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProjectTreasuryReference reference, CancellationToken _) =>
                AZOAResult<ProjectTreasuryAccount>.Success(new ProjectTreasuryAccount(
                    reference.NormalizedChainType, reference.TenantId, reference.NormalizedProjectReference,
                    ExchangeTestHarness.TreasuryAddress, "v1", ProjectTreasuryStatus.Closed)));
        var manager = harness.Build();

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller());

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.OperationNotAllowed);
        harness.Submitter.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task An_account_whose_acceptance_has_not_landed_is_not_sent_units()
    {
        var harness = new ExchangeTestHarness();
        harness.Preparer
            .Setup(preparer => preparer.PrepareAsync(
                It.IsAny<AZOA.WebAPI.Services.Custody.CustodialRecipientPreparation>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<AZOA.WebAPI.Services.Custody.CustodialRecipientReadiness>.Success(
                new AZOA.WebAPI.Services.Custody.CustodialRecipientReadiness(
                    AlreadyPrepared: false, TransactionId: "TX", PendingConfirmation: true,
                    FundedBaseUnits: "100000")));
        var manager = harness.Build();

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller());

        result.Code.Should().Be(ConsumerSafeErrorCodes.SettlementPending);
        harness.Draws.Should().BeEmpty();
        harness.Submitter.Submitted.Should().BeEmpty();
    }

    // ── Nothing settled means nothing left standing ───────────────────────────

    [Fact]
    public async Task A_failed_settlement_hands_the_partition_commitment_back()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(
            submitter: new RecordingExchangeGroupSubmitter(failure: "the rail is unreachable"));

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller());

        result.IsError.Should().BeTrue();
        harness.Draws.Should().HaveCount(2);
        harness.Draws[0].Kind.Should().Be(ReserveDrawKind.Issue);
        harness.Draws[1].Kind.Should().Be(ReserveDrawKind.Return);
        harness.Draws[1].OperationKey.Should().EndWith(
            ":compensate", "the compensating draw needs its own replay anchor");
        harness.Movements.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_settlement_refunds_a_hold_it_had_already_opened()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(
            submitter: new RecordingExchangeGroupSubmitter(failure: "the rail is unreachable"));

        await manager.ExchangeAsync(
            harness.Request(releaseNotBefore: DateTimeOffset.UtcNow.AddDays(30)), harness.Caller());

        harness.HoldCalls.Should().HaveCount(2);
        harness.HoldCalls[0].Verb.Should().Be("open");
        harness.HoldCalls[1].Verb.Should().Be("refund");
    }

    [Fact]
    public async Task The_default_submitter_settles_nothing_and_says_so_without_rail_detail()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(submitter: new UnavailableExchangeGroupSubmitter());

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller());

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(ConsumerSafeErrorCodes.RailUnavailable);
        harness.Movements.Should().BeEmpty("a project is never credited for funds that did not move");
        harness.Draws.Should().HaveCount(2);
        harness.Draws[0].Kind.Should().Be(ReserveDrawKind.Issue);
        harness.Draws[1].Kind.Should().Be(ReserveDrawKind.Return);
    }

    [Fact]
    public async Task An_accepted_but_unconfirmed_group_is_not_reported_as_complete()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(
            submitter: new RecordingExchangeGroupSubmitter(confirmed: false));

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller());

        result.Code.Should().Be(ConsumerSafeErrorCodes.SettlementPending);
        harness.Operations.All.Should().ContainSingle()
            .Which.Status.Should().Be(OperationStatus.PendingConfirmation);
        var ledger = await harness.Idempotency.GetAsync(ExchangeLedgerKey(harness), CancellationToken.None);
        ledger!.State.Should().Be(
            IdempotencyState.InProgress,
            "completing the key would replay a success chain truth has not agreed to");
    }

    // ── The reverse direction ─────────────────────────────────────────────────

    [Fact]
    public async Task Selling_is_refused_and_nothing_is_committed()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();

        var result = await manager.ExchangeAsync(
            harness.Request(direction: ExchangeDirection.Sell), harness.Caller());

        result.IsError.Should().BeTrue();
        result.Message.Should().Be(TreasuryRateExchangePolicy.SellUnavailableMessage);
        harness.Idempotency.RecordCount.Should().Be(0);
        harness.Draws.Should().BeEmpty();
        harness.Movements.Should().BeEmpty();
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_repeated_request_replays_and_moves_nothing_a_second_time()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();

        var first = await manager.ExchangeAsync(harness.Request(), harness.Caller());
        var second = await manager.ExchangeAsync(harness.Request(), harness.Caller());

        first.IsError.Should().BeFalse(first.Message);
        second.IsError.Should().BeFalse(second.Message);
        second.Result!.Replayed.Should().BeTrue();
        second.Result.ReceiptReference.Should().Be(first.Result!.ReceiptReference);
        harness.Submitter.Submitted.Should().ContainSingle("a redelivered request must settle once");
        harness.Movements.Should().ContainSingle();
    }

    [Fact]
    public async Task An_exchange_key_does_not_collide_with_an_allocation_key()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();

        await manager.ExchangeAsync(harness.Request(), harness.Caller("shared-key"));

        harness.Idempotency.Keys.Should().ContainSingle()
            .Which.Should().StartWith(
                "exchange:", "an allocation under the same client key must address a different row");
    }

    [Fact]
    public async Task A_preview_runs_every_gate_and_claims_no_key()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();

        var result = await manager.ExchangeAsync(harness.Request(previewOnly: true), harness.Caller());

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.OperationId.Should().BeNull("an absent operation id is the proof nothing happened");
        harness.Idempotency.RecordCount.Should().Be(0);
        harness.Draws.Should().BeEmpty();
        harness.Submitter.Submitted.Should().BeEmpty();
        harness.Movements.Should().BeEmpty();
    }

    [Fact]
    public async Task A_request_with_no_idempotency_key_is_refused()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller(string.Empty));

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.InvalidRequest);
    }

    // ── The receipt read surface ──────────────────────────────────────────────

    [Fact]
    public async Task The_receipt_route_resolves_the_exchange_for_the_caller_that_ran_it()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();
        await manager.ExchangeAsync(harness.Request(), harness.Caller("sponsorship-9"));

        var receipt = await manager.GetReceiptAsync(new ExchangeReceiptQuery
        {
            ApiKeyId = harness.ApiKeyId,
            CallerUserId = harness.TenantId,
            ClientIdempotencyKey = "sponsorship-9",
        });

        receipt.IsError.Should().BeFalse(receipt.Message);
        receipt.Result!.State.Should().Be(ExchangeReceiptState.Completed);
        receipt.Result.IsTerminal.Should().BeTrue();
        receipt.Result.OperationId.Should().NotBeNull();
        receipt.Result.SettlementReference.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Another_api_key_cannot_read_this_exchange()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();
        await manager.ExchangeAsync(harness.Request(), harness.Caller("sponsorship-9"));

        var receipt = await manager.GetReceiptAsync(new ExchangeReceiptQuery
        {
            ApiKeyId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            CallerUserId = harness.TenantId,
            ClientIdempotencyKey = "sponsorship-9",
        });

        receipt.IsError.Should().BeTrue();
        receipt.Code.Should().Be(AzoaErrorCodes.NotFound);
    }

    [Fact]
    public async Task An_unknown_key_is_not_found_rather_than_an_empty_receipt()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();

        var receipt = await manager.GetReceiptAsync(new ExchangeReceiptQuery
        {
            ApiKeyId = harness.ApiKeyId,
            CallerUserId = harness.TenantId,
            ClientIdempotencyKey = "never-used",
        });

        receipt.IsError.Should().BeTrue();
        receipt.Code.Should().Be(AzoaErrorCodes.NotFound);
    }

    private static string ExchangeLedgerKey(ExchangeTestHarness harness) =>
        $"exchange:{harness.ApiKeyId}:sponsorship-1";
}

internal static class ExchangeHarnessAssertions
{
    /// <summary>The single group the harness's submitter was asked to settle.</summary>
    public static ExchangeGroupPlan Submitted(this ExchangeTestHarness harness) =>
        harness.Submitter.Submitted.Single();
}
