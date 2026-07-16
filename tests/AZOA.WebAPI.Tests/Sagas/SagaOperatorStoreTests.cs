using FluentAssertions;
using AZOA.WebAPI.Models.Sagas;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;

namespace AZOA.WebAPI.Tests.Sagas;

/// <summary>
/// Store-level semantics of the Phase-F operator surface
/// (list / requeue / cancel), driven over <see cref="InMemorySagaStore"/> which
/// mirrors the EXACT conditional single-winner transitions of
/// <see cref="SurrealSagaStore"/>. Proves the revive/cancel guards and
/// idempotency without a live database — the live proof lives in
/// <c>SurrealSagaStoreTests</c>.
/// </summary>
public sealed class SagaOperatorStoreTests
{
    private readonly InMemorySagaStore _store = new();
    private static readonly CancellationToken Ct = CancellationToken.None;

    private async Task<DateTime> ClaimAsync(Guid id)
    {
        var claimed = await _store.TryClaimDueStepAsync(
            id, DateTime.UtcNow.AddSeconds(5), Ct);
        claimed.Should().NotBeNull();
        claimed!.ClaimedAt.Should().NotBeNull();
        return claimed.ClaimedAt!.Value;
    }

    // Drive a step to DeadLettered the way the processor does: enqueue → claim →
    // dead-letter (dead-letter is conditional on InProgress).
    private async Task<SagaStepRecord> SeedDeadLetteredAsync(string saga = "S", string step = "s")
    {
        var enq = await _store.EnqueueAsync(saga, step, $"corr-{Guid.NewGuid():N}",
            $"idem-{Guid.NewGuid():N}", "{}", false, Ct);
        var claimedAt = await ClaimAsync(enq.Id);
        (await _store.DeadLetterStepAsync(enq.Id, claimedAt, "boom", Ct))
            .Should().BeTrue();
        return enq;
    }

    private async Task<SagaStepRecord> SeedParkedAsync()
    {
        var corr = $"corr-{Guid.NewGuid():N}";
        var enq = await _store.EnqueueAsync("quest-workflow", "gate", corr,
            $"idem-{Guid.NewGuid():N}", "{}", false, Ct);
        var claimedAt = await ClaimAsync(enq.Id);
        (await _store.ParkStepAsync(
                enq.Id, claimedAt, "phase-met", resumeAt: null, Ct))
            .Should().BeTrue();
        return enq;
    }

    [Fact]
    public async Task ListByStatuses_ReturnsOnlyMatchingStatuses()
    {
        var dead = await SeedDeadLetteredAsync();
        var parked = await SeedParkedAsync();

        var deadOnly = await _store.ListByStatusesAsync(new[] { StepStatus.DeadLettered }, 100, Ct);
        deadOnly.Select(s => s.Id).Should().Contain(dead.Id);
        deadOnly.Select(s => s.Id).Should().NotContain(parked.Id);

        var both = await _store.ListByStatusesAsync(
            new[] { StepStatus.DeadLettered, StepStatus.Parked }, 100, Ct);
        both.Select(s => s.Id).Should().Contain(new[] { dead.Id, parked.Id });
    }

    [Fact]
    public async Task ListByStatuses_EmptyFilter_ReturnsEmpty()
    {
        await SeedDeadLetteredAsync();
        var result = await _store.ListByStatusesAsync(Array.Empty<StepStatus>(), 100, Ct);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Requeue_DeadLetteredStep_ReturnsToPendingAndDueNow()
    {
        var dead = await SeedDeadLetteredAsync();

        var applied = await _store.RequeueStepAsync(dead.Id, Ct);
        applied.Should().BeTrue();

        var after = await _store.GetAsync(dead.Id, Ct);
        after!.Status.Should().Be(StepStatus.Pending);
        after.DeadLettered.Should().BeFalse("requeue clears the dead-letter mirror");
        after.ClaimedAt.Should().BeNull();

        // Now claimable by the processor again.
        var due = await _store.GetDueStepIdsAsync(
            DateTime.UtcNow.AddSeconds(1), 10, TimeSpan.FromMinutes(5), Ct);
        due.Should().Contain(dead.Id);
    }

    [Fact]
    public async Task Requeue_ParkedStep_ReturnsToPending()
    {
        var parked = await SeedParkedAsync();
        (await _store.RequeueStepAsync(parked.Id, Ct)).Should().BeTrue();
        var after = await _store.GetAsync(parked.Id, Ct);
        after!.Status.Should().Be(StepStatus.Pending);
        after.GateId.Should().BeNull("requeue clears the gate");
    }

    [Fact]
    public async Task Requeue_IsIdempotent_SecondCallNoOps()
    {
        var dead = await SeedDeadLetteredAsync();
        (await _store.RequeueStepAsync(dead.Id, Ct)).Should().BeTrue();
        (await _store.RequeueStepAsync(dead.Id, Ct))
            .Should().BeFalse("a now-Pending row is no longer revivable — zero rows affected");
    }

    [Fact]
    public async Task Requeue_RefusesToReviveCancelledStep()
    {
        var dead = await SeedDeadLetteredAsync();
        (await _store.CancelStepAsync(dead.Id, "operator gave up", Ct)).Should().BeTrue();

        (await _store.RequeueStepAsync(dead.Id, Ct))
            .Should().BeFalse("a Cancelled step is a terminal human decision — requeue must refuse");
        var after = await _store.GetAsync(dead.Id, Ct);
        after!.Status.Should().Be(StepStatus.Cancelled);
    }

    [Fact]
    public async Task Requeue_RefusesCompletedStep()
    {
        var enq = await _store.EnqueueAsync("S", "s", $"corr-{Guid.NewGuid():N}",
            $"idem-{Guid.NewGuid():N}", "{}", false, Ct);
        var claimedAt = await ClaimAsync(enq.Id);
        await _store.CompleteStepAsync(enq.Id, claimedAt, "{}", Ct);

        (await _store.RequeueStepAsync(enq.Id, Ct))
            .Should().BeFalse("a Completed step must never be revived");
    }

    [Fact]
    public async Task Cancel_DeadLetteredStep_MarksCancelledAndRecordsReason()
    {
        var dead = await SeedDeadLetteredAsync();
        (await _store.CancelStepAsync(dead.Id, "duplicate — abandoning", Ct)).Should().BeTrue();

        var after = await _store.GetAsync(dead.Id, Ct);
        after!.Status.Should().Be(StepStatus.Cancelled);
        after.LastError.Should().Be("duplicate — abandoning");
    }

    [Fact]
    public async Task Cancel_PendingStep_Succeeds()
    {
        var enq = await _store.EnqueueAsync("S", "s", $"corr-{Guid.NewGuid():N}",
            $"idem-{Guid.NewGuid():N}", "{}", false, Ct);
        (await _store.CancelStepAsync(enq.Id, "abandon", Ct)).Should().BeTrue();
        (await _store.GetAsync(enq.Id, Ct))!.Status.Should().Be(StepStatus.Cancelled);
    }

    [Fact]
    public async Task Cancel_IsIdempotent_SecondCallNoOps()
    {
        var dead = await SeedDeadLetteredAsync();
        (await _store.CancelStepAsync(dead.Id, "x", Ct)).Should().BeTrue();
        (await _store.CancelStepAsync(dead.Id, "x", Ct))
            .Should().BeFalse("an already-Cancelled step is not re-cancellable");
    }

    [Fact]
    public async Task Cancel_RefusesCompletedStep()
    {
        var enq = await _store.EnqueueAsync("S", "s", $"corr-{Guid.NewGuid():N}",
            $"idem-{Guid.NewGuid():N}", "{}", false, Ct);
        var claimedAt = await ClaimAsync(enq.Id);
        await _store.CompleteStepAsync(enq.Id, claimedAt, "{}", Ct);

        (await _store.CancelStepAsync(enq.Id, "too late", Ct))
            .Should().BeFalse("a Completed step must not be cancellable");
    }

    [Fact]
    public async Task Cancel_LeavesInProgressStepToTheLease()
    {
        var enq = await _store.EnqueueAsync("S", "s", $"corr-{Guid.NewGuid():N}",
            $"idem-{Guid.NewGuid():N}", "{}", false, Ct);
        await _store.TryClaimDueStepAsync(enq.Id, DateTime.UtcNow.AddSeconds(5), Ct);

        (await _store.CancelStepAsync(enq.Id, "no", Ct))
            .Should().BeFalse("an actively-leased InProgress step is left to the lease, not yanked");
        (await _store.GetAsync(enq.Id, Ct))!.Status.Should().Be(StepStatus.InProgress);
    }
}
