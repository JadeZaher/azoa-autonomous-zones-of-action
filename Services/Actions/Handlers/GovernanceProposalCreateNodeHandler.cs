// SPDX-License-Identifier: UNLICENSED

using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.GovernanceProposalCreate"/> — writes the
/// proposal holon that a later <c>GovernanceVote</c> hangs ballots off (D20).
/// </summary>
/// <remarks>
/// <para>
/// TIER-1. <c>RequiresChainCapability</c> stays the default <c>false</c>: a proposal
/// is a holon write, it settles nothing and needs no custody. That is what lets the
/// whole governance lane run in parallel with the KMS workstream.
/// </para>
/// <para>
/// The proposal is PUBLISHED (<c>IsPublic</c>): its electorate is every member who
/// may cast a ballot, and <c>HolonManager.CanRead</c> is owner-or-public, so a
/// private proposal is one only its author can vote on. Ballots are NOT published.
/// </para>
/// <para>
/// The proposal is where the ballot's UNIQUENESS DOMAIN is declared, once, by the
/// proposal author: <c>uniquenessScope</c> plus the derived
/// <c>uniquenessOperationKey</c>. A vote node reads both from here and never from its
/// own config — otherwise each voter would pick their own uniqueness domain and the
/// one-human-one-vote gate would be decorative. See
/// <c>Services/Actions/AGENTS.md</c> §governance.
/// </para>
/// </remarks>
public sealed class GovernanceProposalCreateNodeHandler : IActionNodeHandler
{
    private readonly IHolonManager _holonManager;
    private readonly TimeProvider _clock;

    public GovernanceProposalCreateNodeHandler(IHolonManager holonManager, TimeProvider? clock = null)
    {
        _holonManager = holonManager;
        _clock = clock ?? TimeProvider.System;
    }

    public ActionNodeType NodeType => ActionNodeType.GovernanceProposalCreate;

    // RequiresChainCapability intentionally NOT overridden — stays default false (Tier-1).

    public async Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!ActionNodeConfig.TryDeserialize<GovernanceProposalCreateNodeConfig>(
                context.Node.Config, nameof(ActionNodeType.GovernanceProposalCreate), out var cfg, out var cfgParseError))
            return ActionNodeResults.Fail(cfgParseError);

        var options = cfg.Options
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (string.IsNullOrWhiteSpace(cfg.Name))
            return ActionNodeResults.Invalid("[GovernanceProposalCreate] name is required.");
        if (options.Count < 2)
            return ActionNodeResults.Invalid("[GovernanceProposalCreate] at least two distinct, non-empty options are required.");
        // An option longer than a claim's decision field could be declared here and
        // then never recordable as a vote. Refuse at the proposal, where the author
        // can still fix it, rather than at every ballot.
        if (options.Any(o => PersonUniquenessRequirement.CanonicalizeDecision(o) is null))
            return ActionNodeResults.Invalid(
                $"[GovernanceProposalCreate] every option must be at most {PersonUniquenessRequirement.MaxDecisionLength} characters.");
        if (cfg.OpensAt is { } opens && cfg.ClosesAt is { } closes && closes <= opens)
            return ActionNodeResults.Invalid("[GovernanceProposalCreate] closesAt must be after opensAt.");

        // Scope is parsed with the STRICT parser (spelled names only): a numeric
        // "1" must never widen a tenant ballot to a node-global uniqueness domain.
        var rawScope = string.IsNullOrWhiteSpace(cfg.UniquenessScope)
            ? nameof(PersonUniquenessScope.Tenant)
            : cfg.UniquenessScope!;
        if (!ConsumerGovernanceHolonTypes.TryParseScope(rawScope, out var scope))
            return ActionNodeResults.Invalid($"[GovernanceProposalCreate] uniquenessScope '{rawScope}' is not a recognised scope.");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConsumerGovernanceHolonTypes.ProposalMetadata.Status] = ConsumerGovernanceHolonTypes.StatusOpen,
            [ConsumerGovernanceHolonTypes.ProposalMetadata.Options] = ConsumerGovernanceHolonTypes.SerializeOptions(options),
            [ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessScope] = scope.ToString(),
            // AC5. The electorate is pinned at creation from the ACTING tenant, never
            // from config: a proposal author must not be able to nominate somebody
            // else's members as their electorate. Vote and tally both refuse a caller
            // outside it, with the same sentence they use for "no such proposal".
            [ConsumerGovernanceHolonTypes.ProposalMetadata.ElectorateTenant] =
                ConsumerGovernanceHolonTypes.ElectorateValueFor(context.ActingTenantId),
        };
        if (cfg.OpensAt is { } opensAt)
            metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.OpensAt] = Iso(opensAt);
        if (cfg.ClosesAt is { } closesAt)
            metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.ClosesAt] = Iso(closesAt);

        var created = await _holonManager.CreateAsync(
            new HolonCreateModel
            {
                Name = cfg.Name,
                Description = cfg.Description,
                ParentHolonId = cfg.ParentHolonId,
                AssetType = ConsumerGovernanceHolonTypes.ProposalAssetType,
                Metadata = metadata,
                // A proposal nobody but its author can read is a proposal nobody can
                // vote on: HolonManager.CanRead is owner-or-public, so an unpublished
                // proposal makes GovernanceVote's read fail for every member except
                // the author. Publication is the act of putting the notice up.
                // HolonManager.PublishableAssetTypes keeps this from being a general
                // "any holon may be public" switch, and the RECEIPTS stay private.
                //
                // Publication alone is NODE-wide, because CanRead has no tenant term
                // and that rule lives in a file this lane does not own. So the notice
                // being legible is not the same as being enfranchised: the
                // electorateTenant recorded above is what admits a caller to the vote
                // and the tally, and every governance node checks it. See AC5 in
                // Services/Governance/AGENTS.md §consumer-governance.
                IsPublic = true,
            },
            context.ActingUserId);

        if (created.IsError || created.Result is null)
            return ActionNodeResults.Fail(Reason(created.Message, "the proposal could not be created."));

        var proposalId = created.Result.Id;

        // The uniqueness key is DERIVED from the proposal's own id, so it can only be
        // recorded once the id exists. Record it; if that write fails, delete the
        // half-built proposal rather than leaving one that votes would have to trust.
        var operationKey = ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposalId);
        var recorded = await _holonManager.UpdateAsync(
            proposalId,
            new HolonUpdateModel
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessOperationKey] = operationKey,
                },
            },
            context.ActingUserId);

        if (recorded.IsError || recorded.Result is null)
        {
            await _holonManager.DeleteAsync(proposalId, context.ActingUserId);
            return ActionNodeResults.Fail(
                "the proposal uniqueness key could not be recorded: "
                + Reason(recorded.Message, "unknown error"));
        }

        var output = new Dictionary<string, object?>
        {
            ["proposalId"] = proposalId.ToString(),
            ["status"] = ConsumerGovernanceHolonTypes.StatusOpen,
            ["options"] = options,
            ["opensAt"] = metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.OpensAt),
            ["closesAt"] = metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.ProposalMetadata.ClosesAt),
            ["uniquenessOperationKey"] = operationKey,
            ["uniquenessScope"] = scope.ToString(),
            ["createdAt"] = Iso(_clock.GetUtcNow()),
        };

        return ActionNodeResults.Ok(JsonSerializer.Serialize(output, ActionNodeJson.Options));
    }

    /// <summary>Uses the store's own sentence when it has one; AZOAResult.Message is often empty on error.</summary>
    private static string Reason(string? message, string fallback) =>
        string.IsNullOrWhiteSpace(message) ? fallback : message;

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Config for <see cref="ActionNodeType.GovernanceProposalCreate"/>.
/// </summary>
/// <remarks>
/// Co-located with its handler rather than in <c>Models/Actions/NodeConfigs.cs</c>
/// because the D20 lane does not own that file; move it there (name unchanged) when
/// the <c>ActionNodeConfigRegistry</c> entry lands. See
/// <c>Services/Actions/AGENTS.md</c> §governance.
/// </remarks>
public class GovernanceProposalCreateNodeConfig
{
    /// <summary>Human-readable proposal title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What is being decided.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Optional parent holon (e.g. the co-op the proposal belongs to).</summary>
    public Guid? ParentHolonId { get; set; }

    /// <summary>The ballot choices. At least two distinct, non-empty values.</summary>
    public List<string> Options { get; set; } = new();

    /// <summary>When voting opens. Advisory today; recorded for the tenant and the tally hash.</summary>
    public DateTimeOffset? OpensAt { get; set; }

    /// <summary>When voting closes. Part of the tally preimage, so it pins the tally to a period.</summary>
    public DateTimeOffset? ClosesAt { get; set; }

    /// <summary>
    /// <c>"Tenant"</c> (default) or <c>"Global"</c> — the one-human-one-claim domain
    /// every ballot on this proposal is checked in (D23). A STRING, parsed by
    /// <c>ConsumerGovernanceHolonTypes.TryParseScope</c>, so a numeric <c>1</c>
    /// cannot silently opt a ballot into the node-global domain.
    /// </summary>
    public string? UniquenessScope { get; set; }
}
