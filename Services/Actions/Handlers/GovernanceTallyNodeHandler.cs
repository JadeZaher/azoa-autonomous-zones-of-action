// SPDX-License-Identifier: UNLICENSED

using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.GovernanceTally"/> — publishes the decision a
/// proposal's electorate recorded, counted out of the per-operation claim ledger and
/// nothing else (D20/D39).
/// </summary>
/// <remarks>
/// <para>
/// TIER-1 (D42). <c>RequiresChainCapability</c> stays the default <c>false</c>: the
/// tally is a pure-DB read, needs no custody, and can ship while Phase 3 custody is
/// gated. The <c>tallyHash</c> is still computed and published, so anchoring it on
/// chain later is an added step and not a redesign.
/// </para>
/// <para>
/// <b>The claim IS the vote of record.</b> The shipped tally counted child holons,
/// and <c>POST /api/holon</c> writes those, so it counted forgeries. Joining those
/// holons to the claim ledger was considered and REJECTED: the join still let an
/// attacker-written holon decide which option a claim was spent on, and it stays safe
/// only while nobody drops it. This handler reads
/// <c>person_operation_claim</c> — one row per person per proposal, written inside a
/// single single-winner statement, carrying the chosen option — and reads NO ballot
/// at all. A forged ballot holon therefore counts for nothing by construction.
/// </para>
/// <para>
/// <b>The one holon read is the QUESTION, never the answer.</b> The proposal is read
/// through the consumer-scoped <see cref="IHolonManager"/> for its declared options,
/// its close time, and whether this caller may see it — with the same single sentence
/// <c>GovernanceVote</c> uses, so a tally never becomes the oracle that tells you
/// which proposal ids exist. <see cref="GovernanceVoteOfRecordGuard"/> is checked in
/// the constructor and refuses to let this handler exist if the ledger dependency
/// goes away or a ballot-reading store comes back.
/// </para>
/// <para>
/// <b>The output carries counts, never voters.</b> <c>tallyHash</c> is
/// <see cref="GovernanceTallyHash"/> over (proposalId, closesAt, totalVotes, sorted
/// option counts) and contains no identities — that is the whole reason a co-op
/// tallies off chain instead of putting ballots on it, where every member's choice
/// would be permanently public. A tenant that genuinely needs public verifiability
/// wires <c>GovernanceTally -&gt; BlockchainExecute</c> with <c>$from</c> bound to
/// <c>tallyHash</c>. See <c>Services/Governance/AGENTS.md</c> §consumer-governance.
/// </para>
/// </remarks>
public sealed class GovernanceTallyNodeHandler : IActionNodeHandler
{
    private readonly IHolonManager _holonManager;
    private readonly GovernanceClaimLedgerTally _votesOfRecord;
    private readonly TimeProvider _clock;

    /// <summary>
    /// The proposal comes from the consumer-scoped manager; the DECISIONS come from
    /// the claim ledger. There is deliberately no holon store and no identity store
    /// here: the tally has no business resolving a person, and the guard below turns
    /// that sentence into a control.
    /// </summary>
    public GovernanceTallyNodeHandler(
        IHolonManager holonManager,
        IPersonOperationClaimStore claims,
        TimeProvider? clock = null)
    {
        GovernanceVoteOfRecordGuard.ThrowIfTallyIsNotLedgerOnly();
        _holonManager = holonManager;
        _votesOfRecord = new GovernanceClaimLedgerTally(claims);
        _clock = clock ?? TimeProvider.System;
    }

    public ActionNodeType NodeType => ActionNodeType.GovernanceTally;

    // RequiresChainCapability intentionally NOT overridden — stays default false (Tier-1, D42).

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<GovernanceTallyNodeConfig>(
                context.Node.Config, nameof(ActionNodeType.GovernanceTally), out var cfg, out var cfgParseError))
            return ActionNodeResults.Fail(cfgParseError);

        if (cfg.ProposalHolonId == Guid.Empty)
            return ActionNodeResults.Invalid("[GovernanceTally] proposalHolonId is required.");

        var proposalResult = await _holonManager.GetAsync(cfg.ProposalHolonId, context.ActingUserId);
        if (proposalResult.IsError || proposalResult.Result is null
            || !string.Equals(proposalResult.Result.AssetType, ConsumerGovernanceHolonTypes.ProposalAssetType, StringComparison.Ordinal))
            return ActionNodeResults.Fail(GovernanceVoteNodeHandler.ProposalUnreadable);

        var proposal = proposalResult.Result;

        // AC5. A proposal is published so its own electorate can read it, and
        // HolonManager.CanRead has no tenant term — so the electorate check lives
        // here, and it answers with the SAME sentence as "no such proposal" so a
        // stranger cannot tell a foreign proposal from a nonexistent one.
        if (!ConsumerGovernanceHolonTypes.IsInElectorate(proposal.Metadata, context.ActingTenantId))
            return ActionNodeResults.Fail(GovernanceVoteNodeHandler.ProposalUnreadable);

        if (!ConsumerGovernanceHolonTypes.TryParseOptions(
                proposal.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.Options),
                out var declaredOptions))
            return ActionNodeResults.Fail("the proposal declares no usable option set.");

        // The tally must count in the SAME uniqueness domain the votes were claimed
        // in, and the domain comes from the proposal — read exactly as GovernanceVote
        // reads it, including the derived-key equality check, so a rewritten key
        // cannot point the tally at a domain nobody claimed in. Fail CLOSED.
        if (!ConsumerGovernanceHolonTypes.TryParseScope(
                proposal.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessScope),
                out var scope))
            return ActionNodeResults.Fail("the proposal declares no usable uniqueness scope.");

        var expectedKey = ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposal.Id);
        var declaredKey = proposal.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessOperationKey);
        if (!string.Equals(declaredKey, expectedKey, StringComparison.Ordinal))
            return ActionNodeResults.Fail("the proposal declares no usable uniqueness operation key.");

        // The scope value comes from the proposal's recorded ELECTORATE, not from
        // whoever is running the tally: the count must not move with the caller.
        if (!ConsumerGovernanceHolonTypes.TryResolveUniquenessScopeValue(
                proposal.Metadata, scope, out var uniquenessScopeValue))
            return ActionNodeResults.Fail("the proposal declares no usable electorate.");

        var counted = await _votesOfRecord.CountVotesOfRecordAsync(
            uniquenessScopeValue, proposal.Id, declaredOptions, ct);
        if (counted.IsError || counted.Result is null)
            return ActionNodeResults.Fail(Reason(counted.Message, "the proposal's votes of record could not be read."));

        // Executable form of "the answer came out of the claim ledger". A count that
        // arrived from anywhere else is refused rather than published.
        if (!string.Equals(counted.Result.SourceTable, PersonOperationClaim.SchemaNameConst, StringComparison.Ordinal))
            return ActionNodeResults.Fail("the tally was not counted from the claim ledger.");

        var totalVotes = counted.Result.TotalVotes;
        var counts = counted.Result.CountsByOption;

        var closesAt = proposal.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.ClosesAt);
        var tallyHash = GovernanceTallyHash.Compute(proposal.Id, closesAt, totalVotes, counts);

        var output = new Dictionary<string, object?>
        {
            ["proposalId"] = proposal.Id.ToString(),
            ["totalVotes"] = totalVotes,
            ["counts"] = counts,
            ["tallyHash"] = tallyHash,
            ["tallyAt"] = _clock.GetUtcNow().ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["closesAt"] = closesAt,
        };

        return ActionNodeResults.Ok(JsonSerializer.Serialize(output, ActionNodeJson.Options));
    }

    /// <summary>Uses the store's own sentence when it has one; AZOAResult.Message is often empty on error.</summary>
    private static string Reason(string? message, string fallback) =>
        string.IsNullOrWhiteSpace(message) ? fallback : message;
}

/// <summary>
/// Config for <see cref="ActionNodeType.GovernanceTally"/>.
/// </summary>
/// <remarks>
/// Co-located with its handler rather than in <c>Models/Actions/NodeConfigs.cs</c>
/// because the D20 lane does not own that file; move it there (name unchanged) when
/// the <c>ActionNodeConfigRegistry</c> entry lands. See
/// <c>Services/Actions/AGENTS.md</c> §governance.
/// </remarks>
public class GovernanceTallyNodeConfig
{
    /// <summary>The proposal to count. Usually <c>$from</c>-bound to an upstream GovernanceProposalCreate.</summary>
    public Guid ProposalHolonId { get; set; }
}
