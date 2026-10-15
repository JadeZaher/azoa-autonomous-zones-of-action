// SPDX-License-Identifier: UNLICENSED
//
// OI-20, re-opened. The exchange is the THIRD door onto the reserve partition counters,
// and it was the one the first pass missed: ReserveExchangeManager lifts ProjectAssetId
// and ReservePartition straight off the request body (:141-143) and hands both to
// _reserve.DrawAsync. Its only shape check is length-and-charset, and the node governance
// guard is a chain/asset-TYPE allowlist, not an ownership check.
//
// The seam's obligation is to state a TRUTHFUL authority on every draw. The refusal itself
// lives one level down, at the counter — see PlatformReserveTests "Draw authority".

using System.Numerics;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Exchange;

public class ExchangeDrawAuthorityTests
{
    [Fact]
    public async Task Every_draw_a_buy_makes_states_the_callers_own_tenant()
    {
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller());

        result.IsError.Should().BeFalse(result.Message);
        harness.DrawRequests.Should().NotBeEmpty("a straight-delivery buy draws the named partition");
        harness.DrawRequests.Should().OnlyContain(
            request => request.Authority.ActingTenantId == harness.TenantId
                && !request.Authority.IsNodeOperator
                && !request.Authority.IsSettlingGuardedCommitment,
            "the project asset and the partition both arrive off the caller's body, so the draw "
            + "must carry the caller's own tenant and never an operator or settlement pass");
    }

    [Fact]
    public async Task A_failed_settlement_compensates_under_the_same_authority_it_committed_under()
    {
        // Compensation must never be refused for scope — that would leave the project's
        // earmark permanently spoken for — so it repeats the committing authority rather
        // than reaching for a bypass.
        var harness = new ExchangeTestHarness();
        var manager = harness.Build(submitter: new RecordingExchangeGroupSubmitter(
            confirmed: false, failure: "the rail did not take it"));

        var result = await manager.ExchangeAsync(harness.Request(), harness.Caller());

        result.IsError.Should().BeTrue();
        harness.DrawRequests.Should().HaveCountGreaterThan(1, "the commitment is handed back");
        harness.DrawRequests.Should().OnlyContain(
            request => request.Authority.ActingTenantId == harness.TenantId
                && !request.Authority.IsSettlingGuardedCommitment);
    }

    [Fact]
    public async Task A_held_buy_carries_the_callers_tenant_into_the_hold_that_draws_for_it()
    {
        // The hold primitive draws the partition itself, so the exchange's obligation on
        // this branch is to hand the hold a truthful acting tenant. A null here is exactly
        // OI-21's shape one level removed.
        var harness = new ExchangeTestHarness();
        var manager = harness.Build();

        var result = await manager.ExchangeAsync(
            harness.Request(releaseNotBefore: DateTimeOffset.UtcNow.AddDays(30)), harness.Caller());

        result.IsError.Should().BeFalse(result.Message);
        harness.HoldOpens.Should().ContainSingle()
            .Which.ActingTenantId.Should().Be(harness.TenantId);
        harness.DrawRequests.Should().BeEmpty("the hold draws the partition, so the manager must not draw twice");
    }
}
