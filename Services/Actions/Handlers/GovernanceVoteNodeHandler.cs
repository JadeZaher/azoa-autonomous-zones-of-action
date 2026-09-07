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
/// Handles <see cref="ActionNodeType.GovernanceVote"/> — records ONE human's decision
/// on a proposal, as an in-place write to that human's single uniqueness claim
/// (D3/D20/D23/D39/D40).
/// </summary>
/// <remarks>
/// <para>
/// TIER-1. <c>RequiresChainCapability</c> stays the default <c>false</c>.
/// </para>
/// <para>
/// <b>The claim IS the vote.</b> D39 moved the vote of record off the child holon and
/// onto <c>person_operation_claim</c>: the chosen option travels on the requirement,
/// the gate writes it inside the same single-winner statement that decides the claim,
/// and <c>GovernanceTally</c> reads that row and nothing else. The holon this handler
/// still writes is a RECEIPT for the voter; it carries no authority, and a forged or
/// rewritten one counts for nothing.
/// </para>
/// <para>
/// <b>Mutable until close, last write wins (D40).</b> Voting again before the close
/// is allowed and REPLACES the earlier option, because one row per person means the
/// change is an update of that row rather than a second ballot. There is no "which
/// ballot is latest" question, so the ordering attack the holon tally was vulnerable
/// to does not exist rather than being defended against. After the close the claim is
/// frozen: the gate refuses, and the claim statement refuses again underneath it.
/// </para>
/// <para>
/// <b>The uniqueness domain comes from the PROPOSAL, never from this node's config.</b>
/// The scope and the operation key are read from the proposal holon, and the stored
/// key must equal the key derived from the proposal's own id. A voter-chosen domain
/// would let each voter pick a domain nobody else claims in.
/// </para>
/// <para>
/// <b>The electorate is the proposal's, not the node's (AC5).</b> A proposal is
/// published so its members can read the notice, and <c>HolonManager.CanRead</c> has
/// no tenant term — so this handler refuses a caller outside the electorate the
/// proposal recorded at creation, with the SAME sentence it uses for a proposal that
/// does not exist. See <c>Services/Governance/AGENTS.md</c> §consumer-governance.
/// </para>
/// <para>
/// <b>A receipt NEVER carries a person key</b>, a global person key, or any KYC field.
/// The person key lives only in <c>person_operation_claim</c>, which is
/// node-operator-only; a person key on a receipt is consumer-readable through
/// HolonQuery and instantly reconstitutes the person-to-choice correlation. Receipt
/// metadata is built from the CLOSED
/// <c>ConsumerGovernanceHolonTypes.AllowedVoteMetadataKeys</c> set and re-checked
/// against it before the write.
/// </para>
/// </remarks>
public sealed class GovernanceVoteNodeHandler : IActionNodeHandler
{
    /// <summary>
    /// ONE sentence for "no such proposal", "not readable by you", "not your
    /// electorate", and "that id is not a proposal" — so proposal existence cannot be
    /// probed across users or across tenants.
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

        // The same canonicalization the claim will be written under, so the option
        // that is checked against the proposal is byte-for-byte the option recorded.
        var option = PersonUniquenessRequirement.CanonicalizeDecision(cfg.Option);
        if (option is null)
            return ActionNodeResults.Invalid(
                $"[GovernanceVote] option must be at most {PersonUniquenessRequirement.MaxDecisionLength} characters.");

        // Owner-or-public read, scoped to the RUNNER. HolonManager.GetAsync already
        // answers "not found" for a proposal the runner may not read, and this handler
        // collapses every other rejection onto the same sentence.
        var proposalResult = await _holonManager.GetAsync(cfg.ProposalHolonId, context.ActingUserId);
        if (proposalResult.IsError || proposalResult.Result is null
            || !string.Equals(proposalResult.Result.AssetType, ConsumerGovernanceHolonTypes.ProposalAssetType, StringComparison.Ordinal))
            return ActionNodeResults.Fail(ProposalUnreadable);

        var proposal = proposalResult.Result;

        // AC5, and it must come BEFORE the window and option checks: a stranger who
        // could tell "closed" from "not found" would have an existence oracle again.
        if (!ConsumerGovernanceHolonTypes.IsInElectorate(proposal.Metadata, context.ActingTenantId))
            return ActionNodeResults.Fail(ProposalUnreadable);

        var now = _clock.GetUtcNow();

        var closesAt = TryReadInstant(proposal, ConsumerGovernanceHolonTypes.ProposalMetadata.ClosesAt, out var parsedClose)
            ? parsedClose
            : (DateTimeOffset?)null;

        var windowError = CheckVotingWindow(proposal, now, closesAt);
        if (windowError is not null)
            return ActionNodeResults.Fail(windowError);

        if (!ConsumerGovernanceHolonTypes.TryParseOptions(
                proposal.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.Options),
                out var declaredOptions))
            return ActionNodeResults.Fail("the proposal declares no usable option set.");

        if (!declaredOptions.Contains(option, StringComparer.Ordinal))
            return ActionNodeResults.Fail($"option '{option}' is not one of the proposal's declared options.");

        // The uniqueness domain is the PROPOSAL's, and the stored key must be the one
        // derived from the proposal's own id — a rewritten key would move this vote
        // into a domain nobody else claims in. Fail CLOSED on anything unexpected.
        if (!ConsumerGovernanceHolonTypes.TryParseScope(
                proposal.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessScope),
                out var scope))
            return ActionNodeResults.Fail("the proposal declares no usable uniqueness scope.");

        var expectedKey = ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposal.Id);
        var declaredKey = proposal.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessOperationKey);
        if (!string.Equals(declaredKey, expectedKey, StringComparison.Ordinal))
            return ActionNodeResults.Fail("the proposal declares no usable uniqueness operation key.");

        // D39/D40: the decision rides ON the claim. The gate takes (or re-confirms)
        // the one row this human holds for this proposal and writes the option into it
        // inside that same statement, refusing once the window has shut — so the
        // window is enforced at claim time and not only by CheckVotingWindow above.
        var requirement = new PersonUniquenessRequirement
        {
            OperationKey = expectedKey,
            Scope = scope,
            Decision = option,
            DecisionClosesAt = closesAt,
        };

        var claim = await _uniquenessGate.TryClaimAsync(requirement, context.ActingUserId, context.ActingTenantId, ct);
        if (claim.IsError)
            return ActionNodeResults.Fail(Reason(claim.Message, "the uniqueness gate could not be consulted."));
        if (claim.Result is null || !claim.Result.IsEligible)
            return ActionNodeResults.Fail(Reason(claim.Result?.Message, "this user is not eligible for this operation."));

        // ── from here the vote of record is COMMITTED ────────────────────────────
        // Everything below is the voter's receipt. It is written best-effort and is
        // never compensated by releasing the claim: releasing would discard a recorded
        // vote (and, on a re-vote, a valid earlier one) because a holon write failed.
        var receipt = await WriteReceiptAsync(proposal.Id, option, now, context.ActingUserId);

        var output = new Dictionary<string, object?>
        {
            ["proposalId"] = proposal.Id.ToString(),
            ["voteId"] = receipt?.ToString(),
            ["option"] = option,
            ["castAt"] = Iso(now),
        };

        return ActionNodeResults.Ok(JsonSerializer.Serialize(output, ActionNodeJson.Options));
    }

    /// <summary>
    /// Writes or refreshes the voter's own receipt holon and returns its id, or null
    /// when the platform refused the write. ONE receipt per voter per proposal — a
    /// re-vote updates it, mirroring the single claim row, so nothing in the holarchy
    /// ever looks like two ballots from one person.
    /// </summary>
    private async Task<Guid?> WriteReceiptAsync(Guid proposalId, string option, DateTimeOffset now, Guid runnerUserId)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConsumerGovernanceHolonTypes.VoteMetadata.Option] = option,
            [ConsumerGovernanceHolonTypes.VoteMetadata.CastAt] = Iso(now),
        };

        // Executable form of "a receipt carries the choice and the time, nothing else".
        if (!metadata.Keys.All(ConsumerGovernanceHolonTypes.AllowedVoteMetadataKeys.Contains))
            return null;

        var existing = await FindRunnerReceiptAsync(proposalId, runnerUserId);
        if (existing is { } receiptId)
        {
            var updated = await _holonManager.UpdateAsync(
                receiptId,
                new HolonUpdateModel { Metadata = metadata },
                runnerUserId);

            return updated.IsError || updated.Result is null ? null : receiptId;
        }

        var created = await _holonManager.CreateAsync(
            new HolonCreateModel
            {
                Name = $"Ballot on {proposalId:D}",
                Description = string.Empty,
                ParentHolonId = proposalId,
                AssetType = ConsumerGovernanceHolonTypes.VoteAssetType,
                Metadata = metadata,
            },
            runnerUserId);

        return created.IsError || created.Result is null ? null : created.Result.Id;
    }

    /// <summary>
    /// The runner's own receipt on this proposal, or null. Children are read
    /// RUNNER-SCOPED, which is exactly the scope this question needs: it asks about
    /// the runner's own receipt and must not enumerate anybody else's. A read failure
    /// yields null, which only means a second receipt may be written — the vote of
    /// record is unaffected either way.
    /// </summary>
    private async Task<Guid?> FindRunnerReceiptAsync(Guid proposalId, Guid runnerUserId)
    {
        var children = await _holonManager.GetChildrenAsync(proposalId, runnerUserId);
        if (children.IsError)
            return null;

        return (children.Result ?? Array.Empty<IHolon>())
            .FirstOrDefault(child =>
                string.Equals(child.AssetType, ConsumerGovernanceHolonTypes.VoteAssetType, StringComparison.Ordinal)
                && child.UserId == runnerUserId)
            ?.Id;
    }

    /// <summary>Rejects a vote cast before the proposal opened, after it closed, or once it is Closed.</summary>
    private static string? CheckVotingWindow(IHolon proposal, DateTimeOffset now, DateTimeOffset? closesAt)
    {
        var status = proposal.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.Status);
        if (!string.Equals(status, ConsumerGovernanceHolonTypes.StatusOpen, StringComparison.Ordinal))
            return "the proposal is not open for voting.";

        if (TryReadInstant(proposal, ConsumerGovernanceHolonTypes.ProposalMetadata.OpensAt, out var opensAt) && now < opensAt)
            return "the proposal is not open for voting.";

        if (closesAt is { } closes && now >= closes)
            return "the proposal is closed for voting.";

        return null;
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
