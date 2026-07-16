using AZOA.WebAPI.Models.Sagas;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Tests.Fakes;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Sagas;

public sealed class SagaLeaseOwnershipTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task LeaseGuardedTransitions_RejectWrongToken()
    {
        var store = new InMemorySagaStore();
        var enqueued = await EnqueueAsync(store);
        var claimed = await ClaimAsync(store, enqueued.Id, DateTime.UtcNow.AddSeconds(5));
        var staleClaim = claimed.ClaimedAt!.Value.AddTicks(1);

        (await store.CompleteStepAsync(enqueued.Id, staleClaim, "{}", Ct))
            .Should().BeFalse();
        (await store.ScheduleRetryAsync(
                enqueued.Id, staleClaim, DateTime.UtcNow.AddMinutes(1), "retry", Ct))
            .Should().BeFalse();
        (await store.CompensateStepAsync(
                enqueued.Id, staleClaim, "Undo", $"idem-{Guid.NewGuid():N}",
                "{}", "compensate", Ct))
            .Should().BeNull();
        (await store.DeadLetterStepAsync(enqueued.Id, staleClaim, "dead", Ct))
            .Should().BeFalse();
        (await store.ParkStepAsync(
                enqueued.Id, staleClaim, "gate", resumeAt: null, Ct))
            .Should().BeFalse();

        var persisted = await store.GetAsync(enqueued.Id, Ct);
        persisted!.Status.Should().Be(StepStatus.InProgress);
        persisted.ClaimedAt.Should().Be(claimed.ClaimedAt);
    }

    [Fact]
    public async Task ReclaimedWorker_CannotCompleteOrEnqueueSuccessor()
    {
        var store = new InMemorySagaStore();
        var enqueued = await EnqueueAsync(store);
        var firstClaim = await ClaimAsync(
            store, enqueued.Id, DateTime.UtcNow.AddSeconds(5));
        var reclaimAt = firstClaim.ClaimedAt!.Value.AddMinutes(2);

        var due = await store.GetDueStepIdsAsync(
            reclaimAt, 10, TimeSpan.FromSeconds(1), Ct);
        due.Should().Contain(enqueued.Id);
        var secondClaim = await ClaimAsync(
            store, enqueued.Id, reclaimAt.AddMilliseconds(1));

        var staleSuccessor = await CompleteAndEnqueueAsync(
            store, enqueued, firstClaim.ClaimedAt.Value);
        staleSuccessor.Should().BeNull();
        store.Snapshot().Should().NotContain(
            step => step.Id != enqueued.Id && step.StepName == "Second");

        var winner = await CompleteAndEnqueueAsync(
            store, enqueued, secondClaim.ClaimedAt!.Value);
        winner.Should().NotBeNull();
        store.Snapshot().Should().ContainSingle(
            step => step.Id != enqueued.Id && step.StepName == "Second");
        (await store.GetAsync(enqueued.Id, Ct))!.Status
            .Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task ConcurrentCompletion_CreatesExactlyOneSuccessor()
    {
        var store = new InMemorySagaStore();
        var enqueued = await EnqueueAsync(store);
        var claimed = await ClaimAsync(store, enqueued.Id, DateTime.UtcNow.AddSeconds(5));

        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(
                () => CompleteAndEnqueueAsync(
                    store, enqueued, claimed.ClaimedAt!.Value)))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        results.Should().ContainSingle(result => result != null);
        store.Snapshot().Should().ContainSingle(
            step => step.Id != enqueued.Id && step.StepName == "Second");
    }

    [Fact]
    public async Task InvalidSuccessor_DoesNotCompleteSource()
    {
        var store = new InMemorySagaStore();
        var enqueued = await EnqueueAsync(store);
        var claimed = await ClaimAsync(store, enqueued.Id, DateTime.UtcNow.AddSeconds(5));

        Func<Task> act = async () => await store.CompleteAndEnqueueNextStepAsync(
            enqueued.Id,
            claimed.ClaimedAt!.Value,
            "{}",
            enqueued.SagaName,
            nextStepName: string.Empty,
            enqueued.CorrelationKey,
            $"idem-{Guid.NewGuid():N}",
            enqueued.Payload,
            Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var persisted = await store.GetAsync(enqueued.Id, Ct);
        persisted!.Status.Should().Be(StepStatus.InProgress);
        persisted.ClaimedAt.Should().Be(claimed.ClaimedAt);
        store.Snapshot().Should().ContainSingle();
    }

    private static Task<SagaStepRecord> EnqueueAsync(InMemorySagaStore store)
        => store.EnqueueAsync(
            "LeaseSaga",
            "First",
            $"corr-{Guid.NewGuid():N}",
            $"idem-{Guid.NewGuid():N}",
            "{}",
            isCompensation: false,
            Ct);

    private static async Task<SagaStepRecord> ClaimAsync(
        InMemorySagaStore store,
        Guid id,
        DateTime claimedAt)
    {
        var claimed = await store.TryClaimDueStepAsync(id, claimedAt, Ct);
        claimed.Should().NotBeNull();
        claimed!.ClaimedAt.Should().NotBeNull();
        return claimed;
    }

    private static Task<SagaStepRecord?> CompleteAndEnqueueAsync(
        InMemorySagaStore store,
        SagaStepRecord source,
        DateTime claimedAt)
        => store.CompleteAndEnqueueNextStepAsync(
            source.Id,
            claimedAt,
            """{"first":"done"}""",
            source.SagaName,
            "Second",
            source.CorrelationKey,
            $"idem-{Guid.NewGuid():N}",
            source.Payload,
            Ct);
}
