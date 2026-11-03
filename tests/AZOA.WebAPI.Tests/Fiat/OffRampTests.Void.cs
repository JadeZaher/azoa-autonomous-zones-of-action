using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Fiat;

public partial class OffRampTests
{
    [Fact]
    public async Task Void_is_closed_until_the_operator_attests_all_old_claim_processes_have_drained()
    {
        var harness = new Harness();
        var result = await harness.Service.VoidAsync(SubjectUserId, null, "closed-void");
        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
        harness.Payouts.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_successful_void_survives_response_loss_and_prevents_a_late_request()
    {
        var harness = new Harness { RequestVoidEnabled = true };
        (await harness.Service.VoidAsync(SubjectUserId, null, "lost-answer")).Result!.Outcome.Should().Be("voided");
        (await harness.Service.VoidAsync(SubjectUserId, null, "lost-answer")).Result!.Outcome.Should().Be("voided");
        var delayed = await harness.RequestAsync(500, "lost-answer");
        delayed.IsError.Should().BeTrue();
        delayed.Code.Should().Be("REQUEST_VOIDED");
        harness.Payouts.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Void_reveals_requested_cash_out_instead_of_calling_it_safe_to_discard()
    {
        var harness = new Harness { RequestVoidEnabled = true };
        var admitted = await harness.RequestAsync(500, "admitted");
        admitted.IsError.Should().BeFalse();
        var resolved = await harness.Service.VoidAsync(SubjectUserId, null, "admitted");
        resolved.Result!.Outcome.Should().Be("revealed");
        resolved.Result.CashOut!.Id.Should().Be(admitted.Result!.Id);
        harness.Payouts.Rows.Should().ContainSingle().Which.Status.Should().Be(FiatPayoutStatuses.Requested);
    }
}
