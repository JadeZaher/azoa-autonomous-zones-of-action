using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Models;

public sealed class ConsentLifecycleIdentityTests
{
    [Fact]
    public void Canonical_command_preserves_identity_and_normalizes_scope_sets()
    {
        var command = new ConsentLifecycleCommand(Guid.NewGuid(), Guid.NewGuid(), [" action:execute ", "action:execute"],
            GrantOrigin.UserExplicit, "ignored", DateTimeOffset.Parse("2026-10-15T10:00:00-06:00"));
        var normalized = ConsentLifecycleIdentity.Normalize(command);
        normalized.RequestId.Should().Be(command.RequestId);
        normalized.Scopes.Should().Equal("action:execute");
        normalized.ParticipationRef.Should().BeNull();
        normalized.ExpiresAt!.Value.Offset.Should().Be(TimeSpan.Zero);
        CanonicalJson.Hash(normalized).Should().Be(CanonicalJson.Hash(ConsentLifecycleIdentity.Normalize(command with
            { Scopes = ["action:execute"], ExpiresAt = command.ExpiresAt!.Value.ToUniversalTime() })));
        ConsentLifecycleIdentity.CommandId(Guid.NewGuid(), command.RequestId)
            .Should().NotBe(ConsentLifecycleIdentity.CommandId(Guid.NewGuid(), command.RequestId));
    }

    [Fact]
    public void Participation_rejects_spending_and_preserves_the_credit_floor()
    {
        var command = new ConsentLifecycleCommand(Guid.NewGuid(), Guid.NewGuid(), [AzoaScopes.ActionExecute],
            GrantOrigin.Participation, "membership", null);
        ConsentLifecycleIdentity.Normalize(command).Scopes.Should().Contain(AzoaScopes.ParticipationInboundCreditFloor);
        var forbidden = command with { Scopes = [AzoaScopes.ValueSigningScopes.First()] };
        ((Action)(() => ConsentLifecycleIdentity.Normalize(forbidden))).Should().Throw<ArgumentException>();
        ((Action)(() => ConsentLifecycleIdentity.Normalize(command with { ParticipationRef = " " }))).Should().Throw<ArgumentException>();
        ((Action)(() => ConsentLifecycleIdentity.Normalize(command with { Scopes = ["action:execute,swap:sign"] }))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Transition_identity_is_stable_but_body_binds_the_actual_transition_time()
    {
        var granted = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
        var receipt = new ConsentLifecycleReceipt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            [AzoaScopes.ActionExecute], GrantOrigin.UserExplicit, null, granted, granted.AddDays(1));
        var first = ConsentLifecycleIdentity.Transition(receipt, ConsentWebhookEventType.Revoked, granted.AddMinutes(5));
        var replay = ConsentLifecycleIdentity.Transition(receipt, ConsentWebhookEventType.Revoked, granted.AddMinutes(5));
        replay.Should().Be(first);
        var changed = ConsentLifecycleIdentity.Transition(receipt, ConsentWebhookEventType.Revoked, granted.AddMinutes(6));
        changed.Id.Should().Be(first.Id);
        changed.BodyHash.Should().NotBe(first.BodyHash);
        ConsentLifecycleIdentity.Transition(receipt, ConsentWebhookEventType.Expired, receipt.ExpiresAt!.Value).Id.Should().NotBe(first.Id);
        ((Action)(() => ConsentLifecycleIdentity.Transition(receipt, ConsentWebhookEventType.Expired, granted.AddHours(1))))
            .Should().Throw<ArgumentException>();
        ((Action)(() => ConsentLifecycleIdentity.Transition(receipt, ConsentWebhookEventType.Granted, granted.AddMinutes(1))))
            .Should().Throw<ArgumentException>();
    }
}
