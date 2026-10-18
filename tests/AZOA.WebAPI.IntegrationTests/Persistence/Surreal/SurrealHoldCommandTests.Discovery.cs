using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealHoldCommandTests
{
    [Fact]
    public async Task DiscoverySeparatesOriginalTenantAndActualDirectRecipient()
    {
        var tenant = Guid.NewGuid(); var other = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await Save(new HoldCommand { Id = "direct", ActorUserId = _actor.ToString("N"), State = "AwaitingConsent", CreatedAt = now });
        await Save(new HoldCommand { Id = "tenant", ActorUserId = _actor.ToString("N"), TenantId = tenant.ToString("N"), State = "AwaitingConsent", CreatedAt = now });
        await Save(new HoldCommand { Id = "recipient", ActorUserId = other.ToString("N"), TenantId = tenant.ToString("N"),
            RecipientUserId = _actor.ToString("N"), State = "Admitted", CreatedAt = now });
        await Save(new HoldCommand { Id = "unrelated", ActorUserId = other.ToString("N"), State = "Submitted", CreatedAt = now });
        (await _store.ListParticipantAsync(_actor, null, 0, 20)).Select(row => row.Id).Should().BeEquivalentTo("direct", "recipient");
        (await _store.ListParticipantAsync(_actor, tenant, 0, 20)).Select(row => row.Id).Should().Equal("tenant");
        (await _store.ListParticipantAsync(_actor, Guid.NewGuid(), 0, 20)).Should().BeEmpty();
        (await _store.ListParticipantAsync(_actor, null, 0, 1)).Should().ContainSingle();
        (await _store.ListParticipantAsync(_actor, null, 1, 1)).Should().ContainSingle();
    }

    [Fact]
    public async Task DueScanIncludesOnlyAuthorizedWireAndSchedulingCannotOverrideNewState()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var state in new[] { "AwaitingConsent", "Admitted", "Preparing", "Prepared", "Submitted", "Completed", "FailedNoEffect", "Cancelled" })
            await Save(new HoldCommand { Id = state.ToLowerInvariant(), ActorUserId = _actor.ToString("N"), State = state, CreatedAt = now });
        await Save(new HoldCommand { Id = "acceptance", ActorUserId = _actor.ToString("N"), State = "Admitted", AcceptanceState = "Submitted", CreatedAt = now });
        var due = await _store.ListReconciliationAsync(now, 50);
        due.Select(row => row.Id).Should().BeEquivalentTo("submitted", "acceptance");
        var submitted = due.Single(row => row.Id == "submitted");
        (await _store.ScheduleReconciliationAsync(submitted, now.AddMinutes(1))).Should().BeTrue();
        (await _store.ScheduleReconciliationAsync(submitted, now.AddMinutes(1))).Should().BeFalse();
        var acceptance = due.Single(row => row.Id == "acceptance");
        await Execute(SurrealWriter.UpdateOnly<HoldCommand>(acceptance.Id).Where(seed => true)
            .Set(row => row.AcceptanceState, "Confirmed").Set(row => row.Version, 1L).Build());
        (await _store.ScheduleReconciliationAsync(acceptance, now.AddMinutes(1))).Should().BeFalse();
        (await _store.ListReconciliationAsync(now, 50)).Should().BeEmpty();
        (await _store.ListReconciliationAsync(now.AddMinutes(2), 50)).Should().ContainSingle().Which.Id.Should().Be("submitted");
    }
}
