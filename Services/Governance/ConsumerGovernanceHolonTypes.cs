// SPDX-License-Identifier: UNLICENSED

using System.Text.Json;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyc;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>
/// CONSUMER governance vocabulary (D20): the two Holon asset types a ballot is made
/// of, their metadata keys, and the <c>holon_type_registry</c> seed rows.
/// </summary>
/// <remarks>
/// <para>
/// Not to be confused with the operator-side <see cref="NodeGovernanceAssetTypes"/> /
/// <c>NodeGovernanceGuard</c>: those govern what THIS NODE will run, this file
/// describes what a TENANT'S MEMBERS vote on. See
/// <c>Services/Actions/AGENTS.md</c> §governance.
/// </para>
/// <para>
/// D20 adds NO tables. A proposal is a Holon; a ballot is a child Holon. The
/// person key behind a ballot lives ONLY in <c>person_operation_claim</c>
/// (node-operator-only) — never on a vote holon, which is consumer-readable.
/// </para>
/// </remarks>
public static class ConsumerGovernanceHolonTypes
{
    /// <summary>Holon.AssetType of a proposal (the parent).</summary>
    public const string ProposalAssetType = "GovernanceProposal";

    /// <summary>
    /// Holon.AssetType of a ballot RECEIPT (a child of the proposal). D39 stripped
    /// this holon of all authority: the vote of record is the
    /// <c>person_operation_claim</c> row, the tally never reads this type, and a
    /// forged or rewritten receipt therefore counts for exactly nothing.
    /// </summary>
    public const string VoteAssetType = "GovernanceVote";

    /// <summary>Proposal accepts ballots.</summary>
    public const string StatusOpen = "Open";

    /// <summary>Proposal no longer accepts ballots.</summary>
    public const string StatusClosed = "Closed";

    /// <summary>Metadata keys carried by a proposal holon.</summary>
    public static class ProposalMetadata
    {
        public const string Status = "status";
        public const string Options = "options";
        public const string OpensAt = "opensAt";
        public const string ClosesAt = "closesAt";
        public const string UniquenessOperationKey = "uniquenessOperationKey";
        public const string UniquenessScope = "uniquenessScope";

        /// <summary>The electorate this proposal belongs to: a tenant Guid('N'), or <c>node</c>.</summary>
        public const string ElectorateTenant = "electorateTenant";
    }

    /// <summary>Electorate value of a proposal raised with no tenant context at all.</summary>
    public const string NodeElectorate = "node";

    /// <summary>Metadata keys carried by a vote holon. This list is CLOSED — see <see cref="AllowedVoteMetadataKeys"/>.</summary>
    public static class VoteMetadata
    {
        public const string Option = "option";
        public const string CastAt = "castAt";
    }

    /// <summary>
    /// The COMPLETE set of metadata keys a ballot may carry. The vote handler builds
    /// its metadata from this set and nothing else: a ballot is consumer-readable
    /// through HolonQuery, so every extra field is a public disclosure.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedVoteMetadataKeys =
        new HashSet<string>(StringComparer.Ordinal) { VoteMetadata.Option, VoteMetadata.CastAt };

    /// <summary>
    /// Keys that would put a person identifier on a consumer-readable ballot and so
    /// reconstitute the D2 correlation (person ⇄ choice) the whole design exists to
    /// prevent. Enumerated so a regression test can assert their absence by name;
    /// the real guarantee is <see cref="AllowedVoteMetadataKeys"/> being closed.
    /// </summary>
    public static readonly IReadOnlySet<string> ForbiddenVoteMetadataKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "personKey", "person_key", "globalPersonKey", "global_person_key",
            "kycIdentityId", "kyc_identity_id", "kycReference", "documentNumber", "nationalId",
        };

    /// <summary>
    /// The canonical uniqueness operation key for a proposal:
    /// <c>governance:proposal:{proposalHolonId}</c>. DERIVED, never tenant-authored —
    /// a tenant-chosen key would let two proposals share one uniqueness domain (or
    /// one proposal split into two), which is the same Sybil hole with extra steps.
    /// </summary>
    public static string UniquenessOperationKeyFor(Guid proposalHolonId) =>
        $"governance:proposal:{proposalHolonId:D}".ToLowerInvariant();

    /// <summary>The electorate value a proposal raised under <paramref name="tenantId"/> carries.</summary>
    public static string ElectorateValueFor(Guid? tenantId) =>
        tenantId is null || tenantId.Value == Guid.Empty
            ? NodeElectorate
            : tenantId.Value.ToString("N");

    /// <summary>
    /// True only when the acting tenant is the electorate the proposal recorded at
    /// creation (AC5). FAILS CLOSED on a proposal that records no electorate: an
    /// unrecorded electorate is an unknown one, and the alternative — treating it as
    /// "everybody" — is precisely the node-wide read this repo has leaked through
    /// three times.
    /// </summary>
    public static bool IsInElectorate(IReadOnlyDictionary<string, string> proposalMetadata, Guid? actingTenantId)
    {
        ArgumentNullException.ThrowIfNull(proposalMetadata);
        return proposalMetadata.TryGetValue(ProposalMetadata.ElectorateTenant, out var declared)
            && !string.IsNullOrWhiteSpace(declared)
            && string.Equals(declared, ElectorateValueFor(actingTenantId), StringComparison.Ordinal);
    }

    /// <summary>
    /// The uniqueness scope value a proposal's claims live in, derived from the
    /// ELECTORATE the proposal recorded rather than from whoever happens to be
    /// running the tally. Reuses the one canonical scope rule
    /// (<see cref="KycPersonKeyDeriver.ScopeValue"/>) the gate claims through, so the
    /// vote and the count ask their question in the same domain by construction.
    /// Returns false when the proposal records no usable electorate.
    /// </summary>
    public static bool TryResolveUniquenessScopeValue(
        IReadOnlyDictionary<string, string> proposalMetadata,
        PersonUniquenessScope scope,
        out string uniquenessScopeValue)
    {
        ArgumentNullException.ThrowIfNull(proposalMetadata);
        uniquenessScopeValue = string.Empty;

        if (!proposalMetadata.TryGetValue(ProposalMetadata.ElectorateTenant, out var declared)
            || string.IsNullOrWhiteSpace(declared))
            return false;

        if (scope == PersonUniquenessScope.Global)
        {
            uniquenessScopeValue = KycPersonKeyDeriver.GlobalScopeSentinel;
            return true;
        }

        if (string.Equals(declared, NodeElectorate, StringComparison.Ordinal))
        {
            uniquenessScopeValue = KycPersonKeyDeriver.ScopeValue(null);
            return true;
        }

        if (!Guid.TryParseExact(declared, "N", out var electorateTenantId))
            return false;

        uniquenessScopeValue = KycPersonKeyDeriver.ScopeValue(electorateTenantId);
        return true;
    }

    /// <summary>Serializes the declared option list into the <c>options</c> metadata string.</summary>
    public static string SerializeOptions(IEnumerable<string> options) =>
        JsonSerializer.Serialize(options.ToArray());

    /// <summary>Reads the <c>options</c> metadata string back. Returns false (empty list) when unusable.</summary>
    public static bool TryParseOptions(string? raw, out IReadOnlyList<string> options)
    {
        options = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<string[]>(raw);
            if (parsed is null || parsed.Length == 0 || parsed.Any(string.IsNullOrWhiteSpace))
                return false;
            options = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the <c>uniquenessScope</c> metadata string back. Numeric input is
    /// REJECTED: <c>Enum.TryParse</c> would accept <c>"1"</c> and silently widen a
    /// tenant ballot to node-global, so only the spelled names are accepted.
    /// Returns false when unusable so the caller fails CLOSED.
    /// </summary>
    public static bool TryParseScope(string? raw, out PersonUniquenessScope scope)
    {
        scope = PersonUniquenessScope.Tenant;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();
        if (trimmed.Any(char.IsDigit))
            return false;

        return Enum.TryParse(trimmed, ignoreCase: true, out scope)
            && Enum.IsDefined(scope);
    }

    /// <summary>
    /// The two <c>holon_type_registry</c> rows a tenant seeds to turn the free-string
    /// AssetType into a constrained type. Opt-in (final-hardening F5): unregistered
    /// types are unconstrained, so these rows are what makes a malformed ballot fail
    /// at <c>HolonManager.ValidateAssetTypeAsync</c> rather than at tally time.
    /// <para><c>uniquenessOperationKey</c> is deliberately NOT required on the proposal:
    /// it is derived from the proposal's own id and therefore recorded a beat after
    /// creation.</para>
    /// </summary>
    public static IReadOnlyList<HolonType> SeedRegistrations(DateTimeOffset now) => new[]
    {
        new HolonType
        {
            Id = ProposalAssetType,
            AssetType = ProposalAssetType,
            Description = "Consumer governance proposal (D20). Ballots are child holons; no votes are stored here.",
            RequiredMetadataFields = new List<string>
            {
                ProposalMetadata.Status,
                ProposalMetadata.Options,
                ProposalMetadata.UniquenessScope,
                // AC5: a proposal with no recorded electorate is refused at the
                // registry too, not only by IsInElectorate's fail-closed read.
                ProposalMetadata.ElectorateTenant,
            },
            IsActive = true,
            CreatedAt = now,
        },
        new HolonType
        {
            Id = VoteAssetType,
            AssetType = VoteAssetType,
            Description = "Receipt for one ballot on a governance proposal (D20/D39). Carries the choice and the time only — never a person key — and carries NO authority: the vote of record is the person_operation_claim row and the tally never reads this type.",
            RequiredMetadataFields = new List<string>
            {
                VoteMetadata.Option,
                VoteMetadata.CastAt,
            },
            IsActive = true,
            CreatedAt = now,
        },
    };
}
