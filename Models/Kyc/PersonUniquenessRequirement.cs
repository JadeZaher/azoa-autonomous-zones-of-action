// SPDX-License-Identifier: UNLICENSED

#nullable enable

using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Models.Kyc;

/// <summary>Uniqueness domain a per-operation claim is enforced in (D23).</summary>
public enum PersonUniquenessScope
{
    /// <summary>
    /// One claim per human per TENANT. Deliberately value 0 so an omitted JSON
    /// field, a default-constructed record, and a null-coalesced value all land
    /// here: the D23 default must not be reachable by forgetting.
    /// </summary>
    Tenant = 0,

    /// <summary>
    /// One claim per human across the whole node. Reachable ONLY by an explicit
    /// <c>"scope": "Global"</c>; the gate logs every resolution at Warning so an
    /// operator can audit each cross-tenant question the node was ever asked.
    /// </summary>
    Global = 1,
}

/// <summary>
/// An operation's declared one-human-one-claim requirement (D3/D23). Null = not
/// declared = no check, which is the default for every existing node type.
/// </summary>
public sealed record PersonUniquenessRequirement
{
    /// <summary>Longest accepted canonical operation key.</summary>
    public const int MaxOperationKeyLength = 200;

    /// <summary>Longest accepted decision the claim may carry.</summary>
    public const int MaxDecisionLength = 200;

    /// <summary>
    /// Tenant-authored, stable identifier of the claimable thing (e.g.
    /// <c>governance:proposal:{holonId}</c>). Canonicalized by
    /// <see cref="Canonicalize"/>.
    /// </summary>
    public required string OperationKey { get; init; }

    /// <summary>D23: per-tenant unless the operator explicitly opts into node-global.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PersonUniquenessScope Scope { get; init; } = PersonUniquenessScope.Tenant;

    /// <summary>
    /// D39/D40: the decision this claim is spent on, carried ON the claim row so the
    /// claim IS the vote of record. Null means the operation records no decision —
    /// every non-governance caller — and the claim behaves exactly as it did before.
    /// A re-claim by the same holder with a different decision is an IN-PLACE update
    /// of the one row, so there is never a second ballot and never an ordering rule
    /// to attack.
    /// </summary>
    public string? Decision { get; init; }

    /// <summary>
    /// D40: the instant after which this operation's decision is frozen. Enforced by
    /// the gate before the claim AND inside the claim statement itself, so a caller
    /// that forgets its own window check still cannot record a decision late. Null
    /// means the operation never closes.
    /// </summary>
    public DateTimeOffset? DecisionClosesAt { get; init; }

    /// <summary>
    /// Trims, lower-cases invariantly, and REJECTS anything outside
    /// <c>[a-z0-9:._-]</c> or longer than <see cref="MaxOperationKeyLength"/>.
    /// Rejecting rather than stripping or truncating is deliberate: stripping
    /// would collapse <c>"a b"</c> and <c>"ab"</c> into one uniqueness domain and
    /// truncation would merge two long keys that share a 200-character prefix,
    /// silently letting one operation consume another's claims. Returns null when
    /// unusable so the caller fails CLOSED.
    /// </summary>
    public static string? Canonicalize(string? operationKey)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
            return null;

        var canonical = operationKey.Trim().ToLowerInvariant();
        if (canonical.Length == 0 || canonical.Length > MaxOperationKeyLength)
            return null;

        foreach (var character in canonical)
        {
            var allowed = character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9'
                || character is ':' or '.' or '_' or '-';
            if (!allowed)
                return null;
        }

        return canonical;
    }

    /// <summary>
    /// Trims a decision and REJECTS anything empty or longer than
    /// <see cref="MaxDecisionLength"/>. Deliberately does NOT lower-case or strip:
    /// the decision has to compare Ordinal-equal to an option the proposal declared,
    /// and a canonicalizer that rewrote it would quietly count a vote for an option
    /// nobody offered. Returns null when unusable so the caller fails CLOSED.
    /// </summary>
    public static string? CanonicalizeDecision(string? decision)
    {
        if (string.IsNullOrWhiteSpace(decision))
            return null;

        var trimmed = decision.Trim();
        return trimmed.Length == 0 || trimmed.Length > MaxDecisionLength ? null : trimmed;
    }
}
