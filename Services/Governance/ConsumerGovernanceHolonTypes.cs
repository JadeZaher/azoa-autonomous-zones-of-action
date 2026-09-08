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

    /// <summary>
    /// What <see cref="ElectorateValueFor"/> mints for a proposal raised with no tenant
    /// context at all. RECOGNISED SO IT CAN BE REFUSED (W9.2): a plain user JWT carries
    /// no <c>act_as_tenant</c> claim, so an ordinary member raising a proposal would
    /// otherwise enfranchise every tenant-less verified human on the node — and do it in
    /// the same person-key domain node-global claims use, because
    /// <see cref="KycPersonKeyDeriver.ScopeValue"/> of a null tenant IS
    /// <see cref="KycPersonKeyDeriver.GlobalScopeSentinel"/>.
    /// <see cref="ProposalElectorateRejection"/> refuses it at the holon chokepoint.
    /// See <c>Services/Governance/AGENTS.md</c> §electorate-must-name-a-tenant.
    /// </summary>
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
    /// True only for an electorate that NAMES ONE TENANT. <see cref="NodeElectorate"/>
    /// and anything unparseable are false: an electorate nobody chose is not a smaller
    /// electorate, it is the whole node.
    /// </summary>
    public static bool IsUsableElectorateValue(string? declared) =>
        !string.IsNullOrWhiteSpace(declared)
        && !string.Equals(declared, NodeElectorate, StringComparison.Ordinal)
        && Guid.TryParseExact(declared, "N", out var electorate)
        && electorate != Guid.Empty;

    /// <summary>
    /// The refusal a proposal earns when it RECORDS an electorate that names no tenant,
    /// or null when there is nothing to refuse. Applied by <c>HolonManager</c> on every
    /// write and on the reads both governance nodes perform, so the node electorate
    /// cannot be minted through <c>GovernanceProposalCreate</c> OR through a hand-written
    /// <c>POST /api/holon</c>, and a row written before this rule is never handed back as
    /// a live notice.
    /// </summary>
    /// <remarks>
    /// A proposal that records NO electorate at all is deliberately left alone: it is
    /// already inert, because <see cref="IsInElectorate"/> and
    /// <see cref="TryResolveUniquenessScopeValue"/> both fail closed on it, so nobody can
    /// vote on it or tally it. Only a recorded-but-unusable electorate is a live claim to
    /// an audience the author never had.
    /// </remarks>
    public static string? ProposalElectorateRejection(
        string? assetType,
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (!string.Equals(assetType, ProposalAssetType, StringComparison.Ordinal))
            return null;

        if (metadata is null
            || !metadata.TryGetValue(ProposalMetadata.ElectorateTenant, out var declared)
            || string.IsNullOrWhiteSpace(declared))
            return null;

        return IsUsableElectorateValue(declared)
            ? null
            : "A governance proposal's electorate must name one tenant; "
              + $"'{declared}' names the whole node.";
    }

    /// <summary>
    /// True only when the acting tenant is the electorate the proposal recorded at
    /// creation (AC5). FAILS CLOSED on a proposal that records no electorate: an
    /// unrecorded electorate is an unknown one, and the alternative — treating it as
    /// "everybody" — is precisely the node-wide read this repo has leaked through
    /// three times.
    /// </summary>
    /// <remarks>
    /// This comparison still ADMITS a proposal recording <see cref="NodeElectorate"/> to a
    /// caller with no acting tenant, and that is not the guarantee — the guarantee is that
    /// such a proposal cannot be written or read back through <c>HolonManager</c>
    /// (<see cref="ProposalElectorateRejection"/>), so the value cannot reach here from
    /// storage. Narrowing this predicate as well is the belt to that braces and is
    /// deliberately NOT done in W9.2: it would rewrite the expectations of ~18 tests in
    /// <c>tests/AZOA.WebAPI.Tests/Actions/Handlers/GovernanceNodeHandlerTests.cs</c>, a
    /// file that item does not own. See <c>Services/Governance/AGENTS.md</c>
    /// §electorate-must-name-a-tenant for the exact follow-up edit.
    /// </remarks>
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

    /// <summary>
    /// The proposal metadata a recorded vote DEPENDS ON: the question, the domain the
    /// claims live in, and the window. Frozen from the first vote of record onward — see
    /// <see cref="DescribeDecisionBearingRewrite"/> and
    /// <c>Services/Governance/AGENTS.md</c> §proposal-freeze.
    /// </summary>
    /// <remarks>
    /// <c>name</c> and <c>description</c> are deliberately ABSENT: fixing a typo in the
    /// title moves no outcome, and a freeze that blocked it would be dropped the first
    /// time it got in somebody's way.
    /// </remarks>
    public static readonly IReadOnlySet<string> FrozenProposalMetadataKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ProposalMetadata.Status,
            ProposalMetadata.Options,
            ProposalMetadata.OpensAt,
            ProposalMetadata.ClosesAt,
            ProposalMetadata.UniquenessOperationKey,
            ProposalMetadata.UniquenessScope,
            ProposalMetadata.ElectorateTenant,
        };

    /// <summary>
    /// The claim domain a vote on this proposal WOULD have been recorded in, or false
    /// when the proposal cannot hold a claim at all.
    /// </summary>
    /// <remarks>
    /// False is a PROOF, not a guess: <c>GovernanceVoteNodeHandler</c> refuses before it
    /// reaches the gate unless the stored uniqueness key equals the key derived from the
    /// proposal's own id, the scope parses by its spelled name, and the recorded
    /// electorate resolves — exactly the three conditions below. A proposal failing any of
    /// them has no claim to freeze, which is also why a freshly created proposal (its
    /// derived key not yet recorded) can still receive that one write.
    /// </remarks>
    public static bool TryResolveClaimDomain(
        Guid proposalHolonId,
        IReadOnlyDictionary<string, string> proposalMetadata,
        out string uniquenessScopeValue,
        out string operationKey)
    {
        ArgumentNullException.ThrowIfNull(proposalMetadata);
        uniquenessScopeValue = string.Empty;
        operationKey = string.Empty;

        var expectedKey = UniquenessOperationKeyFor(proposalHolonId);
        if (!string.Equals(
                proposalMetadata.GetValueOrDefault(ProposalMetadata.UniquenessOperationKey),
                expectedKey,
                StringComparison.Ordinal))
            return false;

        // The SAME canonicalization the claim was written under, so the lookup asks for
        // the rows the gate actually wrote rather than for a key that matches nothing.
        var canonicalKey = PersonUniquenessRequirement.Canonicalize(expectedKey);
        if (canonicalKey is null)
            return false;

        if (!TryParseScope(proposalMetadata.GetValueOrDefault(ProposalMetadata.UniquenessScope), out var scope))
            return false;

        if (!TryResolveUniquenessScopeValue(proposalMetadata, scope, out uniquenessScopeValue))
            return false;

        operationKey = canonicalKey;
        return true;
    }

    /// <summary>
    /// Names the decision-bearing parts of a proposal an incoming write would CHANGE, or
    /// null when it changes none of them. Compares values, so re-sending what is already
    /// recorded stays idempotent and a retry is never mistaken for a rewrite.
    /// </summary>
    /// <param name="recordedMetadata">The proposal's metadata BEFORE the merge.</param>
    /// <param name="recordedIsPublic">The proposal's publication BEFORE the merge.</param>
    /// <param name="recordedAssetType">The proposal's asset type BEFORE the merge.</param>
    /// <param name="incomingMetadata">Caller metadata; blind-merged by the manager.</param>
    /// <param name="incomingIsPublic">Caller publication flag, or null when untouched.</param>
    /// <param name="incomingAssetType">Caller asset type, or null when untouched.</param>
    /// <param name="removedMetadataKeys">
    /// Keys the caller is DELETING (the holon-interaction route can). Deleting the close
    /// time or the electorate moves the outcome exactly as rewriting it does, and a
    /// value-only comparison would never see it.
    /// </param>
    public static string? DescribeDecisionBearingRewrite(
        IReadOnlyDictionary<string, string> recordedMetadata,
        bool recordedIsPublic,
        string? recordedAssetType,
        IReadOnlyDictionary<string, string>? incomingMetadata,
        bool? incomingIsPublic,
        string? incomingAssetType,
        IEnumerable<string>? removedMetadataKeys = null)
    {
        ArgumentNullException.ThrowIfNull(recordedMetadata);

        var changed = new List<string>();

        if (incomingMetadata is not null)
        {
            foreach (var key in FrozenProposalMetadataKeys)
            {
                if (incomingMetadata.TryGetValue(key, out var incoming)
                    && !string.Equals(recordedMetadata.GetValueOrDefault(key), incoming, StringComparison.Ordinal))
                    changed.Add(key);
            }
        }

        if (removedMetadataKeys is not null)
        {
            foreach (var key in removedMetadataKeys)
            {
                if (FrozenProposalMetadataKeys.Contains(key)
                    && recordedMetadata.ContainsKey(key)
                    && !changed.Contains(key, StringComparer.Ordinal))
                    changed.Add(key);
            }
        }

        // Renaming the type away from GovernanceProposal makes every vote already
        // recorded unreachable: both governance nodes check the asset type first.
        if (incomingAssetType is not null
            && !string.Equals(incomingAssetType, recordedAssetType, StringComparison.Ordinal))
            changed.Add("assetType");

        // Un-publishing takes the notice — and with it the tally, which reads the
        // proposal owner-or-public — away from everyone except the author.
        if (incomingIsPublic is { } isPublic && isPublic != recordedIsPublic)
            changed.Add("isPublic");

        // Sorted so the refusal a caller sees does not depend on hash-set order.
        return changed.Count == 0
            ? null
            : string.Join(", ", changed.OrderBy(key => key, StringComparer.Ordinal));
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
