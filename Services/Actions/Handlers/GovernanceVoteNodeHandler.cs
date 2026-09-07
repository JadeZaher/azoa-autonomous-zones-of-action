// SPDX-License-Identifier: UNLICENSED

using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.GovernanceVote"/> — casts ONE ballot on a
/// proposal, after the one-human-one-claim gate says this human has not already
/// voted through another user (D3/D20/D23).
/// </summary>
/// <remarks>
/// <para>
/// TIER-1. <c>RequiresChainCapability</c> stays the default <c>false</c>: a ballot is
/// a holon write.
/// </para>
/// <para>
/// <b>The uniqueness consultation is the point of this handler.</b> Verified users are
/// cheap to multiply; without <see cref="IPersonUniquenessGate.TryClaimAsync"/> one
/// human holding N users votes N times and "one member one vote" is decorative. The
/// claim runs BEFORE the ballot is written, and a ballot write that fails afterwards
/// RELEASES the claim — otherwise a transient store fault would disenfranchise the
/// voter permanently.
/// </para>
/// <para>
/// <b>The uniqueness domain comes from the PROPOSAL, never from this node's config.</b>
/// The scope and the operation key are read from the proposal holon's metadata, and
/// the stored key must equal the key derived from the proposal's own id
/// (<c>ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor</c>). A voter-chosen
/// domain would let each voter pick a domain nobody else claims in.
/// </para>
/// <para>
/// <b>A ballot NEVER carries a person key</b>, a global person key, or any KYC field.
/// The person key lives only in <c>person_operation_claim</c>, which is
/// node-operator-only; a person key on a vote child is consumer-readable through
/// HolonQuery and instantly reconstitutes the person-to-choice correlation. The ballot
/// metadata is built from the CLOSED
/// <c>ConsumerGovernanceHolonTypes.AllowedVoteMetadataKeys</c> set and re-checked
/// against it before the write. See <c>Services/Actions/AGENTS.md</c> §governance.
/// </para>
/// </remarks>
public sealed class GovernanceVoteNodeHandler : IActionNodeHandler
{
    /// <summary>
    /// ONE sentence for "no such proposal", "not readable by you", and "that id is
    /// not a proposal" — so proposal existence cannot be probed across users.
    /// </summary>
    internal const string ProposalUnreadable = "governance proposal not found.";

    private readonly IHolonManager _holonManager;
    private readonly IPersonUniquenessGate _uniquenessGate;
    private readonly TimeProvider _clock;

    public GovernanceVoteNodeHandler(
        IHolonManager holonManager,
        IPersonUniquenessGate uniquenessGate,
        TimeProvider? clock = null)
    {
        _holonManager = holonManager;
        _uniquenessGate = uniquenessGate;
        _clock = clock ?? TimeProvider.System;
    }

    public ActionNodeType NodeType => ActionNodeType.GovernanceVote;

    // RequiresChainCapability intentionally NOT overridden — stays default false (Tier-1).

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<GovernanceVoteNodeConfig>(
                context.Node.Config, nameof(ActionNodeType.GovernanceVote), out var cfg, out var cfgParseError))
            return ActionNodeResults.Fail(cfgParseError);

        if (cfg.ProposalHolonId == Guid.Empty)
            return ActionNodeResults.Invalid("[GovernanceVote] proposalHolonId is required.");
        if (string.IsNullOrWhiteSpace(cfg.Option))
            return ActionNodeResults.Invalid("[GovernanceVote] option is required.");

        var option = cfg.Option.Trim();

        // Owner-or-public read, scoped to the RUNNER. HolonManager.GetAsync already
        // answers "Holon not found." for a proposal the runner may not read, and this
        // handler collapses every other rejection onto the same sentence.
        var proposalResult = await _holonManager.GetAsync(cfg.ProposalHolonId, context.ActingUserId);
        if (proposalResult.IsError || proposalResult.Result is null
            || !string.Equals(proposalResult.Result.AssetType, ConsumerGovernanceHolonTypes.ProposalAssetType, StringComparison.Ordinal))
            return ActionNodeResults.Fail(ProposalUnreadable);

        var proposal = proposalResult.Result;
        var now = _clock.GetUtcNow();

        var windowError = CheckVotingWindow(proposal, now);
        if (windowError is not null)
            return ActionNodeResults.Fail(windowError);

        if (!ConsumerGovernanceHolonTypes.TryParseOptions(
                proposal.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.Options),
                out var declaredOptions))
            return ActionNodeResults.Fail("the proposal declares no usable option set.");

        if (!declaredOptions.Contains(option, StringComparer.Ordinal))
            return ActionNodeResults.Fail($"option '{option}' is not one of the proposal's declared options.");

        // The uniqueness domain is the PROPOSAL's, and the stored key must be the one
        // derived from the proposal's own id — a rewritten key would move this ballot
        // into a domain nobody else claims in. Fail CLOSED on anything unexpected.
        if (!ConsumerGovernanceHolonTypes.TryParseScope(
                proposal.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessScope),
                out var scope))
            return ActionNodeResults.Fail("the proposal declares no usable uniqueness scope.");

        var expectedKey = ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposal.Id);
        var declaredKey = proposal.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessOperationKey);
        if (!string.Equals(declaredKey, expectedKey, StringComparison.Ordinal))
            return ActionNodeResults.Fail("the proposal declares no usable uniqueness operation key.");

        // One ballot per USER, checked before the claim so a repeat vote costs no
        // claim round trip. The one-ballot-per-HUMAN answer is the gate's, below.
        var duplicateError = await FindRunnerBallotProblemAsync(proposal.Id, context.ActingUserId);
        if (duplicateError is not null)
            return ActionNodeResults.Fail(duplicateError);

        var requirement = new PersonUniquenessRequirement
        {
            OperationKey = expectedKey,
            Scope = scope,
        };

        var claim = await _uniquenessGate.TryClaimAsync(requirement, context.ActingUserId, context.ActingTenantId, ct);
        if (claim.IsError)
            return ActionNodeResults.Fail(Reason(claim.Message, "the uniqueness gate could not be consulted."));
        if (claim.Result is null || !claim.Result.IsEligible)
            return ActionNodeResults.Fail(Reason(claim.Result?.Message, "this user is not eligible for this operation."));

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConsumerGovernanceHolonTypes.VoteMetadata.Option] = option,
            [ConsumerGovernanceHolonTypes.VoteMetadata.CastAt] = Iso(now),
        };

        // Executable form of "a ballot carries the choice and the time, nothing else".
        if (!metadata.Keys.All(ConsumerGovernanceHolonTypes.AllowedVoteMetadataKeys.Contains))
        {
            await _uniquenessGate.ReleaseAsync(requirement, context.ActingUserId, context.ActingTenantId, ct);
            return ActionNodeResults.Invalid("[GovernanceVote] ballot metadata carries a field outside the allowed set.");
        }

        var created = await _holonManager.CreateAsync(
            new HolonCreateModel
            {
                Name = $"Ballot on {proposal.Id:D}",
                Description = string.Empty,
                ParentHolonId = proposal.Id,
                AssetType = ConsumerGovernanceHolonTypes.VoteAssetType,
                Metadata = metadata,
            },
            context.ActingUserId);

        if (created.IsError || created.Result is null)
        {
            // Compensate: a held claim with no ballot behind it silently
            // disenfranchises this human for the rest of the proposal.
            await _uniquenessGate.ReleaseAsync(requirement, context.ActingUserId, context.ActingTenantId, ct);
            return ActionNodeResults.Fail(Reason(created.Message, "the ballot could not be recorded."));
        }

        var output = new Dictionary<string, object?>
        {
            ["proposalId"] = proposal.Id.ToString(),
            ["voteId"] = created.Result.Id.ToString(),
            ["option"] = option,
            ["castAt"] = metadata[ConsumerGovernanceHolonTypes.VoteMetadata.CastAt],
        };

        return ActionNodeResults.Ok(JsonSerializer.Serialize(output, ActionNodeJson.Options));
    }

    /// <summary>Rejects a ballot cast before the proposal opened, after it closed, or once it is Closed.</summary>
    private static string? CheckVotingWindow(IHolon proposal, DateTimeOffset now)
    {
        var status = proposal.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.Status);
        if (!string.Equals(status, ConsumerGovernanceHolonTypes.StatusOpen, StringComparison.Ordinal))
            return "the proposal is not open for voting.";

        if (TryReadInstant(proposal, ConsumerGovernanceHolonTypes.ProposalMetadata.OpensAt, out var opensAt) && now < opensAt)
            return "the proposal is not open for voting.";

        if (TryReadInstant(proposal, ConsumerGovernanceHolonTypes.ProposalMetadata.ClosesAt, out var closesAt) && now >= closesAt)
            return "the proposal is closed for voting.";

        return null;
    }

    /// <summary>
    /// Null when the runner may still cast a ballot; otherwise the refusal sentence.
    /// Children are read RUNNER-SCOPED, which is exactly the scope this question
    /// needs: it asks about the runner's own ballots and must not enumerate anybody
    /// else's. A read failure is a refusal, not a pass.
    /// </summary>
    private async Task<string?> FindRunnerBallotProblemAsync(Guid proposalId, Guid runnerUserId)
    {
        var children = await _holonManager.GetChildrenAsync(proposalId, runnerUserId);
        if (children.IsError)
            return "the proposal's existing ballots could not be read.";

        var duplicate = (children.Result ?? Array.Empty<IHolon>()).Any(child =>
            string.Equals(child.AssetType, ConsumerGovernanceHolonTypes.VoteAssetType, StringComparison.Ordinal)
            && child.UserId == runnerUserId);

        return duplicate ? "this user has already voted on this proposal." : null;
    }

    /// <summary>Uses the store's own sentence when it has one; AZOAResult.Message is often empty on error.</summary>
    private static string Reason(string? message, string fallback) =>
        string.IsNullOrWhiteSpace(message) ? fallback : message;

    private static bool TryReadInstant(IHolon holon, string key, out DateTimeOffset instant)
    {
        instant = default;
        var raw = holon.Metadata.GetValueOrDefault(key);
        return !string.IsNullOrWhiteSpace(raw)
            && DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out instant);
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Config for <see cref="ActionNodeType.GovernanceVote"/>.
/// </summary>
/// <remarks>
/// There is deliberately NO uniqueness scope or operation key here: both come from
/// the proposal (see the handler's remarks). Co-located with its handler rather than
/// in <c>Models/Actions/NodeConfigs.cs</c> because the D20 lane does not own that
/// file; move it there (name unchanged) when the <c>ActionNodeConfigRegistry</c>
/// entry lands. See <c>Services/Actions/AGENTS.md</c> §governance.
/// </remarks>
public class GovernanceVoteNodeConfig
{
    /// <summary>The proposal being voted on. Usually <c>$from</c>-bound to an upstream GovernanceProposalCreate.</summary>
    public Guid ProposalHolonId { get; set; }

    /// <summary>The chosen option. Must be one of the proposal's declared options.</summary>
    public string Option { get; set; } = string.Empty;
}
