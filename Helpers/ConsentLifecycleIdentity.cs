using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Helpers;

/// <summary>Canonical consent command and transition identities; see Helpers/AGENTS.md.</summary>
public static class ConsentLifecycleIdentity
{
    public static ConsentLifecycleCommand Normalize(ConsentLifecycleCommand command)
    {
        if (command.RequestId == Guid.Empty || command.TenantId == Guid.Empty || command.Scopes is null
            || !Enum.IsDefined(command.Origin)) throw new ArgumentException("Consent command identity is invalid.");
        var scopes = command.Scopes.Where(scope => !string.IsNullOrWhiteSpace(scope)).Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (scopes.Length == 0 || scopes.Any(scope => scope.Contains(',') || scope.Any(char.IsControl)))
            throw new ArgumentException("Consent scopes must be nonempty canonical scope names.");
        var participation = command.ParticipationRef?.Trim();
        if (command.Origin == GrantOrigin.Participation)
        {
            if (string.IsNullOrWhiteSpace(participation) || participation.Length > 1024 || scopes.Any(AzoaScopes.ValueSigningScopes.Contains))
                throw new ArgumentException("Participation requires a reference and cannot authorize value signing.");
            scopes = scopes.Concat(AzoaScopes.ParticipationInboundCreditFloor).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            if (scopes.Any(AzoaScopes.ValueSigningScopes.Contains))
                throw new InvalidOperationException("Participation credit floor contains a value-signing scope.");
        }
        else participation = null;
        return command with { Scopes = scopes, ParticipationRef = participation, ExpiresAt = command.ExpiresAt?.ToUniversalTime() };
    }

    public static string CommandId(Guid grantor, Guid requestId)
    {
        if (grantor == Guid.Empty || requestId == Guid.Empty) throw new ArgumentException("Consent command identity is empty.");
        return CanonicalJson.Hash(new { Domain = "consent-command-v1", Grantor = grantor, RequestId = requestId });
    }

    public static string ParticipationId(Guid tenantId, string participationRef)
    {
        if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(participationRef) || participationRef.Trim().Length > 1024)
            throw new ArgumentException("Participation boundary identity is invalid.");
        return CanonicalJson.Hash(new { Domain = "consent-participation-v1", TenantId = tenantId, ParticipationRef = participationRef.Trim() });
    }

    public static string ApprovalId(string membershipId, Guid grantor)
        => CanonicalJson.Hash(new { Domain = "consent-enrollment-v1", MembershipId = membershipId, Grantor = grantor });

    public static ConsentTransitionSnapshot Transition(ConsentLifecycleReceipt grant, ConsentWebhookEventType type,
        DateTimeOffset occurredAt)
    {
        if (grant.GrantId == Guid.Empty || grant.GrantorUserId == Guid.Empty || grant.TenantId == Guid.Empty
            || !Enum.IsDefined(type)) throw new ArgumentException("Consent transition identity is invalid.");
        occurredAt = occurredAt.ToUniversalTime();
        if ((type == ConsentWebhookEventType.Granted && occurredAt != grant.GrantedAt)
            || (type == ConsentWebhookEventType.Expired && occurredAt != grant.ExpiresAt)
            || occurredAt < grant.GrantedAt) throw new ArgumentException("Consent transition time does not match its persisted fact.");
        var id = CanonicalJson.Hash(new { Domain = "consent-transition-v1", grant.GrantId, Type = type.ToString() })[..32];
        var body = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            eventType = "consent." + type.ToString().ToLowerInvariant(), grantId = grant.GrantId.ToString("D"),
            userId = grant.GrantorUserId.ToString("D"), tenantId = grant.TenantId.ToString("D"),
            scopes = grant.Scopes.ToArray(), participationRef = grant.ParticipationRef,
            occurredAt = occurredAt.ToString("O"), idempotencyId = id,
        }));
        return new(id, body, CanonicalJson.HashCanonical(body), occurredAt);
    }
}
