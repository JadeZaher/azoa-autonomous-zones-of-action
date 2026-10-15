using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Node types that are implemented and dispatchable but must not run yet.</summary>
/// <remarks>
/// See <c>Services/Actions/AGENTS.md</c> §not-enabled-node-types.
///
/// This exists because "not enabled" was, until now, a claim rather than a control.
/// The Phase 5 governance and credential rails were described as not enabled in the
/// runbook, the track specs and the builder UI — while <c>Program.cs</c> assembly-scans
/// every <see cref="Interfaces.Actions.IActionNodeHandler"/> in the Handlers namespace
/// and registers it, the config registry accepts their configs, and both dispatch seams
/// would have executed them. A published action reaching GovernanceVote WOULD have cast
/// a ballot; GovernanceTally WOULD have tallied a count its own design cannot trust;
/// CredentialRevoke WOULD have attempted an Algorand clawback. The builder's "preview"
/// badge was frontend copy and nothing more.
///
/// That is the same defect class this codebase keeps shipping — a control that exists
/// only where nobody enforces it — so the refusal is placed where the chain-capability
/// gate already proved the pattern: fail closed at BOTH dispatch seams, plus publish.
/// </remarks>
public static class NotEnabledActionNodeTypes
{
    /// <summary>
    /// Governance cannot be trusted to hold a franchise, and the credential rail cannot
    /// authorize (the only mint path stamps issuer = holder, making the issuer check
    /// vacuous). Tracked by <c>governance-vote-of-record_20260906</c> and
    /// <c>credential-third-party-issuance_20260906</c>; remove a member from this set
    /// only when its track closes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The governance reason CHANGED, and saying so is the point. It used to be "the
    /// tally reads raw holons, so a ballot created outside the handler is counted
    /// without ever taking a uniqueness claim". That defect is FIXED — <c>fa2dfcf</c>
    /// moved the vote of record onto <c>person_operation_claim</c> (D39), and the
    /// independent review re-proved it: <c>GovernanceVoteNodeHandler</c> is the only
    /// production caller of <c>IPersonUniquenessGate.TryClaimAsync</c> in the tree, and
    /// <c>GovernanceClaimLedgerTally</c> reads that ledger and nothing else. A gate
    /// left standing on a reason that has stopped being true is itself the defect class
    /// this file exists to end, so the reason is restated rather than inherited.
    /// </para>
    /// <para>
    /// The three governance types stay blocked on the CURRENT findings, recorded in
    /// <c>conductor/tracks/ardanova-production-readiness-waves_20260907/REVIEW-GOVERNANCE-VERDICT.md</c>
    /// (verdict FAIL): the electorate is bound to nobody — <c>ProposalElectorateRejection</c>
    /// checks only that the declared <c>electorateTenant</c> parses as a tenant Guid, and
    /// <c>HolonManager</c> has no tenant context with which to check WHOSE, so any dApp
    /// developer may raise a proposal naming another tenant's members; and there is no
    /// close authority at all — <c>closesAt</c> is optional at creation and frozen from
    /// the first ballot, so a proposal raised without one accepts ballots forever and can
    /// be neither closed nor deleted.
    /// </para>
    /// <para>
    /// This set and that verdict file are held together by
    /// <c>tests/AZOA.WebAPI.Tests/Actions/NotEnabledActionNodeTypesTests.cs</c>: flipping
    /// one without the other goes red. Lift the gate by fixing the findings, changing the
    /// verdict line, and removing the members — in one change.
    /// </para>
    /// </remarks>
    private static readonly HashSet<ActionNodeType> Blocked = new()
    {
        ActionNodeType.GovernanceProposalCreate,
        ActionNodeType.GovernanceVote,
        ActionNodeType.GovernanceTally,
        ActionNodeType.CredentialRevoke,
    };

    /// <summary>True when this node type is implemented but must not execute yet.</summary>
    public static bool IsBlocked(ActionNodeType nodeType) => Blocked.Contains(nodeType);

    /// <summary>The blocked set, for tests and the catalog surface.</summary>
    public static IReadOnlySet<ActionNodeType> All => Blocked;

    /// <summary>One refusal string, so publish and both dispatch seams read identically.</summary>
    public static string RefusalMessage(ActionNodeType nodeType) =>
        $"Node type '{nodeType}' is implemented but not enabled: it has a known correctness "
        + "defect and is gated until its design track closes. See conductor/tracks.md.";
}
