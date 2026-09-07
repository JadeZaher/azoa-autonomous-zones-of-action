// SPDX-License-Identifier: UNLICENSED

using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.GovernanceTally"/> — counts the ballots hanging
/// off a proposal and publishes an identity-free digest of the result (D20).
/// </summary>
/// <remarks>
/// <para>
/// TIER-1. <c>RequiresChainCapability</c> stays the default <c>false</c>: the tally
/// runs in a pure-DB node, needing no chain capability and no custody.
/// </para>
/// <para>
/// <b>A ballot counts only when a live uniqueness claim backs it.</b> Ballots are
/// ordinary child holons and <c>POST /api/holon</c> writes them, so counting child
/// holons counts forgeries: one verified user can mint five hundred
/// <c>GovernanceVote</c> children and never touch <c>GovernanceVoteNodeHandler</c>.
/// The count therefore comes from <see cref="GovernanceBallotRoll"/>, which joins
/// each ballot to the node-operator-only <c>person_operation_claim</c> row for THIS
/// proposal's uniqueness domain and spends each claim at most once. Delete that join
/// and <c>Tally_CountsOnlyClaimBackedBallots_AndIgnoresForgedOnes</c> fails.
/// </para>
/// <para>
/// <b>The output carries counts, never voters.</b> <c>tallyHash</c> is
/// <see cref="GovernanceTallyHash"/> over (proposalId, closesAt, totalVotes, sorted
/// option counts) and contains no voter identities — that is the whole reason a
/// co-op tallies off chain instead of putting ballots on it, where every member's
/// choice would be permanently public.
/// </para>
/// <para>
/// <b>No anchor node.</b> A tenant that genuinely needs public verifiability wires
/// <c>GovernanceTally -&gt; BlockchainExecute</c> with <c>$from</c> bound to
/// <c>tallyHash</c>, one transaction per proposal. See
/// <c>Services/Actions/AGENTS.md</c> §governance.
/// </para>
/// </remarks>
public sealed class GovernanceTallyNodeHandler : IActionNodeHandler
{
    private readonly IHolonManager _holonManager;
    private readonly GovernanceBallotRoll _ballots;
    private readonly TimeProvider _clock;

    /// <summary>
    /// The proposal is read through the consumer-scoped manager (it is published, so
    /// every member may read it); the BALLOTS are read through
    /// <see cref="GovernanceBallotRoll"/>, which owns the privileged unscoped read
    /// and returns counts only. The three stores it needs are already registered, so
    /// the assembly-scanned handler registration keeps working unchanged.
    /// </summary>
    public GovernanceTallyNodeHandler(
        IHolonManager holonManager,
        IHolonStore holonStore,
        IKycIdentityStore identities,
        IPersonOperationClaimStore claims,
        TimeProvider? clock = null)
    {
        _holonManager = holonManager;
        _ballots = new GovernanceBallotRoll(holonStore, identities, claims);
        _clock = clock ?? TimeProvider.System;
    }

    public ActionNodeType NodeType => ActionNodeType.GovernanceTally;

    // RequiresChainCapability intentionally NOT overridden — stays default false (Tier-1).

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<GovernanceTallyNodeConfig>(
                context.Node.Config, nameof(ActionNodeType.GovernanceTally), out var cfg, out var cfgParseError))
            return ActionNodeResults.Fail(cfgParseError);

        if (cfg.ProposalHolonId == Guid.Empty)
            return ActionNodeResults.Invalid("[GovernanceTally] proposalHolonId is required.");

        // The PROPOSAL is read consumer-scoped, with the same single sentence as
        // GovernanceVote: a tally must not become the oracle that tells you which
        // proposal ids exist. The BALLOTS are not read here at all — see the roll.
        var proposalResult = await _holonManager.GetAsync(cfg.ProposalHolonId, context.ActingUserId);
        if (proposalResult.IsError || proposalResult.Result is null
            || !string.Equals(proposalResult.Result.AssetType, ConsumerGovernanceHolonTypes.ProposalAssetType, StringComparison.Ordinal))
            return ActionNodeResults.Fail(GovernanceVoteNodeHandler.ProposalUnreadable);

        var proposal = proposalResult.Result;

        if (!ConsumerGovernanceHolonTypes.TryParseOptions(
                proposal.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.Options),
                out var declaredOptions))
            return ActionNodeResults.Fail("the proposal declares no usable option set.");

        // The tally must count in the SAME uniqueness domain the ballots claimed in,
        // and the domain comes from the proposal — read exactly as GovernanceVote
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

        // Claim-backed count. Every declared option comes back, including the ones
        // nobody chose: a count that silently omits a zero would change the hash
        // preimage as votes arrive.
        var counted = await _ballots.CountClaimBackedBallotsAsync(
            proposal.Id, scope, context.ActingTenantId, declaredOptions, ct);
        if (counted.IsError || counted.Result is null)
            return ActionNodeResults.Fail(Reason(counted.Message, "the proposal's ballots could not be read."));

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
