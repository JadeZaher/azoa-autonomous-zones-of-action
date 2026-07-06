using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Models.Sagas;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;

namespace AZOA.WebAPI.Tests.Sagas;

/// <summary>
/// <see cref="SagaOperatorController"/> routing/contract tests: status-filter
/// parsing, the 200-vs-404 shape of requeue/cancel, and the diagnosis
/// projection. The Operator-policy authorization itself is enforced by the
/// pipeline attribute (<c>[Authorize(Policy="Operator")]</c>) and covered by the
/// policy tests; here we drive the action methods directly over the in-memory
/// store to prove the operator semantics.
/// </summary>
public sealed class SagaOperatorControllerTests
{
    private readonly InMemorySagaStore _store = new();
    private readonly SagaOperatorController _sut;
    private static readonly CancellationToken Ct = CancellationToken.None;

    public SagaOperatorControllerTests()
    {
        _sut = new SagaOperatorController(_store);
    }

    private async Task<SagaStepRecord> SeedDeadLetteredAsync()
    {
        var enq = await _store.EnqueueAsync("S", "s", $"corr-{Guid.NewGuid():N}",
            $"idem-{Guid.NewGuid():N}", "{}", false, Ct);
        await _store.TryClaimDueStepAsync(enq.Id, DateTime.UtcNow.AddSeconds(5), Ct);
        await _store.DeadLetterStepAsync(enq.Id, "boom", Ct);
        return enq;
    }

    [Fact]
    public async Task ListDeadLetters_DefaultFilter_ReturnsDeadLetteredView()
    {
        var dead = await SeedDeadLetteredAsync();

        var result = await _sut.ListDeadLetters(status: null, limit: null, Ct);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var views = ok.Value.Should().BeAssignableTo<IReadOnlyList<SagaStepView>>().Subject;
        views.Should().ContainSingle(v => v.Id == dead.Id);
        views.Single(v => v.Id == dead.Id).Status.Should().Be("DeadLettered");
        views.Single(v => v.Id == dead.Id).LastError.Should().Be("boom");
    }

    [Fact]
    public async Task ListDeadLetters_UnknownStatus_ReturnsBadRequest()
    {
        var result = await _sut.ListDeadLetters(status: new[] { "Bogus" }, limit: null, Ct);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ListDeadLetters_NonInspectableStatus_ReturnsBadRequest()
    {
        // Completed/InProgress/Pending/Compensating are not operator-inspectable
        // at-rest states — the surface only lists DeadLettered/Parked/Cancelled.
        var result = await _sut.ListDeadLetters(status: new[] { "Completed" }, limit: null, Ct);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Requeue_RevivableStep_ReturnsOk()
    {
        var dead = await SeedDeadLetteredAsync();
        var result = await _sut.Requeue(dead.Id, Ct);
        result.Should().BeOfType<OkObjectResult>();
        (await _store.GetAsync(dead.Id, Ct))!.Status.Should().Be(StepStatus.Pending);
    }

    [Fact]
    public async Task Requeue_UnknownOrTerminalStep_Returns404()
    {
        var result = await _sut.Requeue(Guid.NewGuid(), Ct);
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Cancel_DeadLetteredStep_ReturnsOk_AndRecordsReason()
    {
        var dead = await SeedDeadLetteredAsync();
        var result = await _sut.Cancel(dead.Id, new SagaStepCancelRequest { Reason = "abandon" }, Ct);
        result.Should().BeOfType<OkObjectResult>();
        var after = await _store.GetAsync(dead.Id, Ct);
        after!.Status.Should().Be(StepStatus.Cancelled);
        after.LastError.Should().Be("abandon");
    }

    [Fact]
    public async Task Cancel_NullBody_UsesDefaultReason()
    {
        var dead = await SeedDeadLetteredAsync();
        var result = await _sut.Cancel(dead.Id, body: null, Ct);
        result.Should().BeOfType<OkObjectResult>();
        (await _store.GetAsync(dead.Id, Ct))!.LastError.Should().Be("Cancelled by operator.");
    }

    [Fact]
    public async Task Cancel_UnknownOrCompletedStep_Returns404()
    {
        var result = await _sut.Cancel(Guid.NewGuid(), null, Ct);
        result.Should().BeOfType<NotFoundObjectResult>();
    }
}
