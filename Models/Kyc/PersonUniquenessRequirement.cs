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
}
