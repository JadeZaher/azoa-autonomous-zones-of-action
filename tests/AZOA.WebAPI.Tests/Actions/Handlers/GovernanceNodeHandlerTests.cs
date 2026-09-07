// SPDX-License-Identifier: UNLICENSED

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Kyc;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;
// Aliased, never imported wholesale: AZOA.WebAPI.Persistence.SurrealDb.Models also
// declares a Holon, and importing that namespace would make every Holon in this file
// ambiguous.
using PersonOperationClaim = AZOA.WebAPI.Persistence.SurrealDb.Models.PersonOperationClaim;

namespace AZOA.WebAPI.Tests.Actions.Handlers;

/// <summary>
/// Dispatch tests for the D20 consumer-governance nodes: proposal create, vote, tally.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing assertions are the Sybil ones. <c>GovernanceVote</c> MUST consult
/// <see cref="IPersonUniquenessGate"/> before anything is recorded, in the uniqueness
/// domain the PROPOSAL declared — remove the consultation and
/// <c>Vote_Refused_WhenGateSaysIneligible_AndNoBallotIsWritten</c> plus
/// <c>Vote_ClaimsUniqueness_BeforeWritingBallot</c> both fail.
/// </para>
/// <para>
/// <b>D39: the claim row IS the vote of record.</b> The tally counts
/// <c>person_operation_claim</c> and reads NO ballot holon, so the holon a vote leaves
/// behind is a RECEIPT that carries no authority. The tests here therefore seed
/// decisions into the LEDGER and use holons only as forgeries. The claim-JOINED
/// ballot tally this file once pinned was considered and REJECTED: the join still let
/// an attacker-written holon decide WHICH option a claim was spent on, and it stayed
/// safe only while nobody dropped it.
/// </para>
/// <para>
/// The other load-bearing assertion is that a receipt carries the choice and the time
/// and NOTHING else: a person key on a consumer-readable vote holon reconstitutes the
/// person-to-choice correlation the whole design exists to prevent.
/// </para>
/// <para>
/// The behavioural attack tests for the same design — the 500-holon bypass, hostile
/// castAt ordering, re-vote replacement, the post-close freeze, and the AC5 electorate
/// — live in <c>tests/AZOA.WebAPI.Tests/Governance</c> and run the REAL
/// <c>HolonManager</c>. This is the unit/dispatch lane; it does not restate them.
/// </para>
/// </remarks>
public class GovernanceNodeHandlerTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Voter = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Stranger = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    // ── harness ───────────────────────────────────────────────────────────────

    /// <summary>Mirrors <c>HolonManager.CanRead</c>: owner-or-public, null caller fails closed.</summary>
    private static bool CanRead(Holon holon, Guid? caller) =>
        caller.HasValue && holon.UserId == caller.Value || holon.IsPublic;

    private static Mock<IHolonManager> HolonWorld(
        List<Holon> store,
        List<string>? trace = null,
        Func<HolonCreateModel, string?>? createError = null,
        Func<Guid, string?>? updateError = null)
    {
        var mock = new Mock<IHolonManager>();

        mock.Setup(m => m.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((Guid id, Guid? caller, AZOARequest? _) =>
            {
                var holon = store.FirstOrDefault(h => h.Id == id);
                return holon is null || !CanRead(holon, caller)
                    ? new AZOAResult<IHolon> { IsError = true, Message = "Holon not found." }
                    : new AZOAResult<IHolon> { Result = holon };
            });

        mock.Setup(m => m.CreateAsync(It.IsAny<HolonCreateModel>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((HolonCreateModel model, Guid userId, AZOARequest? _) =>
            {
                trace?.Add($"create:{model.AssetType}");
                var error = createError?.Invoke(model);
                if (error is not null)
                    return new AZOAResult<IHolon> { IsError = true, Message = error };

                var holon = new Holon
                {
                    Id = Guid.NewGuid(),
                    Name = model.Name,
                    Description = model.Description,
                    ParentHolonId = model.ParentHolonId,
                    AssetType = model.AssetType,
                    UserId = userId,
                    Metadata = new Dictionary<string, string>(model.Metadata),
                    IsPublic = model.IsPublic,
                };
                store.Add(holon);
                return new AZOAResult<IHolon> { Result = holon };
            });

        mock.Setup(m => m.UpdateAsync(It.IsAny<Guid>(), It.IsAny<HolonUpdateModel>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((Guid id, HolonUpdateModel model, Guid? userId, AZOARequest? _) =>
            {
                var error = updateError?.Invoke(id);
                if (error is not null)
                    return new AZOAResult<IHolon> { IsError = true, Message = error };

                var holon = store.FirstOrDefault(h => h.Id == id);
                if (holon is null || userId.HasValue && holon.UserId != userId.Value)
                    return new AZOAResult<IHolon> { IsError = true, Message = "Holon not found." };

                foreach (var (key, value) in model.Metadata ?? new Dictionary<string, string>())
                    holon.Metadata[key] = value;
                return new AZOAResult<IHolon> { Result = holon };
            });

        mock.Setup(m => m.GetChildrenAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((Guid parentId, Guid? caller, AZOARequest? _) =>
                new AZOAResult<IEnumerable<IHolon>>
                {
                    Result = store.Where(h => h.ParentHolonId == parentId && CanRead(h, caller)).Cast<IHolon>().ToList(),
                });

        mock.Setup(m => m.DeleteAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((Guid id, Guid? _, AZOARequest? __) =>
            {
                trace?.Add("delete");
                store.RemoveAll(h => h.Id == id);
                return new AZOAResult<bool> { Result = true };
            });

        return mock;
    }

    private static Mock<IPersonUniquenessGate> Gate(
        List<PersonUniquenessRequirement> claimed,
        bool eligible = true,
        List<string>? trace = null,
        List<PersonUniquenessRequirement>? released = null)
    {
        var mock = new Mock<IPersonUniquenessGate>();

        mock.Setup(g => g.TryClaimAsync(
                It.IsAny<PersonUniquenessRequirement>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonUniquenessRequirement req, Guid _, Guid? __, CancellationToken ___) =>
            {
                trace?.Add("claim");
                claimed.Add(req);
                return new AZOAResult<KycEligibilityDecision>
                {
                    Result = eligible
                        ? KycEligibilityDecision.Eligible()
                        : KycEligibilityDecision.Ineligible(KycIneligibilityCause.PersonAlreadyClaimedOperation),
                };
            });

        mock.Setup(g => g.ReleaseAsync(
                It.IsAny<PersonUniquenessRequirement>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonUniquenessRequirement req, Guid _, Guid? __, CancellationToken ___) =>
            {
                trace?.Add("release");
                released?.Add(req);
                return new AZOAResult<bool> { Result = true };
            });

        return mock;
    }

    private static ActionNodeExecutionContext Ctx(ActionNodeType type, object config, Guid actingUserId, Guid? tenantId = null)
    {
        var node = new ActionNode
        {
            Id = Guid.NewGuid(),
            Name = type.ToString(),
            NodeType = type,
            Config = JsonSerializer.Serialize(config),
        };
        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            UserId = Owner,
            Nodes = new List<ActionNode> { node },
            Edges = new List<ActionEdge>(),
        };
        return new ActionNodeExecutionContext(Guid.NewGuid(), node.Id, action, actingUserId, null, tenantId);
    }

    private static TimeProvider ClockAt(DateTimeOffset instant) => new FixedTimeProvider(instant);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// A proposal exactly as <c>GovernanceProposalCreate</c> leaves it, INCLUDING the
    /// electorate it records. <paramref name="isPublic"/> models readability (a private
    /// proposal is readable only by its author) and <paramref name="electorateTenant"/>
    /// the AC5 case: both the vote node and the tally refuse a caller outside the
    /// recorded electorate, with the same sentence they use for a proposal that does
    /// not exist. The electorate key is written unconditionally because
    /// <c>IsInElectorate</c> FAILS CLOSED on a proposal that records none — seeding it
    /// without one would make every assertion below pass for the wrong reason.
    /// </summary>
    private static Holon SeedProposal(
        List<Holon> store,
        Guid ownerId,
        string[] options,
        string scope = "Tenant",
        string status = "Open",
        DateTimeOffset? closesAt = null,
        bool isPublic = true,
        Guid? electorateTenant = null)
    {
        var proposal = new Holon
        {
            Id = Guid.NewGuid(),
            Name = "Proposal",
            AssetType = ConsumerGovernanceHolonTypes.ProposalAssetType,
            UserId = ownerId,
            IsPublic = isPublic,
        };
        proposal.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.Status] = status;
        proposal.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.Options] =
            ConsumerGovernanceHolonTypes.SerializeOptions(options);
        proposal.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessScope] = scope;
        proposal.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessOperationKey] =
            ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposal.Id);
        proposal.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.ElectorateTenant] =
            ConsumerGovernanceHolonTypes.ElectorateValueFor(electorateTenant);
        if (closesAt is { } closes)
            proposal.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.ClosesAt] =
                closes.ToUniversalTime().ToString("O");

        store.Add(proposal);
        return proposal;
    }

    /// <summary>
    /// A ballot exactly as <c>GovernanceVote</c> leaves it: owned by its voter and
    /// NOT public, because a published ballot would carry <c>Holon.UserId</c> beside
    /// the choice. Nothing here proves the ballot went through the handler, which is
    /// the point: this is also what a forged POST /api/holon ballot looks like.
    /// </summary>
    private static Holon SeedBallot(
        List<Holon> store, Guid proposalId, Guid voterId, string option, DateTimeOffset? castAt = null)
    {
        var ballot = new Holon
        {
            Id = Guid.NewGuid(),
            Name = $"Ballot on {proposalId:D}",
            ParentHolonId = proposalId,
            AssetType = ConsumerGovernanceHolonTypes.VoteAssetType,
            UserId = voterId,
        };
        ballot.Metadata[ConsumerGovernanceHolonTypes.VoteMetadata.Option] = option;
        ballot.Metadata[ConsumerGovernanceHolonTypes.VoteMetadata.CastAt] = (castAt ?? Now).ToString("O");
        store.Add(ballot);
        return ballot;
    }

    // -- the tally's world since D39: the claim ledger, and nothing else --

    /// <summary>
    /// The node-operator-only claim ledger, keyed exactly as the gate writes it. Only
    /// <c>ListLiveByOperationAsync</c> is set up, because it is the ONLY read the D39
    /// tally performs: keyed by (scope, operation), naming no person and no caller, so
    /// the read cannot be turned into a person-key oracle.
    /// </summary>
    /// <param name="includeReleased">
    /// Models a store that FORGETS its own contract and hands back released rows, so
    /// the tally's own <c>ReleasedAt</c> re-check is exercised rather than assumed.
    /// </param>
    private static Mock<IPersonOperationClaimStore> LedgerOf(
        List<PersonOperationClaim> claims, string? readError = null, bool includeReleased = false)
    {
        var mock = new Mock<IPersonOperationClaimStore>();
        mock.Setup(s => s.ListLiveByOperationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string scope, string operationKey, CancellationToken _) =>
                readError is not null
                    ? new AZOAResult<IReadOnlyList<PersonOperationClaim>> { IsError = true, Message = readError }
                    : new AZOAResult<IReadOnlyList<PersonOperationClaim>>
                    {
                        Result = claims
                            .Where(c => c.UniquenessScope == scope
                                && c.OperationKey == operationKey
                                && (includeReleased || c.ReleasedAt is null))
                            .ToList(),
                    });
        return mock;
    }

    /// <summary>
    /// A vote of record: the ONE claim row a human holds on a proposal, carrying the
    /// option they chose (D39). This — never a holon — is what the tally counts.
    /// <paramref name="tenantId"/> is the uniqueness domain the claim was taken in,
    /// which the tally derives from the proposal's ELECTORATE rather than from
    /// whoever happens to run the count.
    /// </summary>
    private static PersonOperationClaim SeedVoteOfRecord(
        List<PersonOperationClaim> claims,
        Guid proposalId,
        string personKey,
        Guid holder,
        string? decision = null,
        Guid? tenantId = null,
        bool released = false)
    {
        var operationKey = ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposalId);
        var scope = KycPersonKeyDeriver.ScopeValue(tenantId);
        var claim = new PersonOperationClaim
        {
            Id = PersonOperationClaim.RecordIdFor(scope, operationKey, personKey),
            UniquenessScope = scope,
            OperationKey = operationKey,
            PersonKey = personKey,
            UserId = holder.ToString("N"),
            ClaimedAt = Now,
            ReleasedAt = released ? Now : null,
            DecisionOption = decision,
            DecidedAt = decision is null ? null : Now,
        };
        claims.Add(claim);
        return claim;
    }

    /// <summary>
    /// The tally as production wires it: the consumer-scoped holon manager for the
    /// QUESTION (the proposal's options, close time, electorate and readability) and
    /// the claim ledger for the ANSWER. There is deliberately no identity store and no
    /// holon store — the tally resolves no person, and
    /// <see cref="GovernanceVoteOfRecordGuard"/> refuses to construct a tally holding
    /// either.
    /// </summary>
    private static GovernanceTallyNodeHandler TallyHandler(
        List<Holon> store,
        List<PersonOperationClaim> claims,
        string? ledgerReadError = null) =>
        new(HolonWorld(store).Object, LedgerOf(claims, ledgerReadError).Object, ClockAt(Now));

    private static JsonElement Output(ActionNodeHandlerResult result)
    {
        result.IsError.Should().BeFalse(result.Message ?? string.Empty);
        return JsonDocument.Parse(result.Output!).RootElement.Clone();
    }

    // ── tier / identity ───────────────────────────────────────────────────────

    [Fact]
    public void AllThreeGovernanceHandlers_AreTier1_AndMapToTheirNodeType()
    {
        var store = new List<Holon>();
        IActionNodeHandler create = new GovernanceProposalCreateNodeHandler(HolonWorld(store).Object);
        IActionNodeHandler vote = new GovernanceVoteNodeHandler(
            HolonWorld(store).Object, Gate(new List<PersonUniquenessRequirement>()).Object);
        IActionNodeHandler tally = TallyHandler(store, new List<PersonOperationClaim>());

        create.NodeType.Should().Be(ActionNodeType.GovernanceProposalCreate);
        vote.NodeType.Should().Be(ActionNodeType.GovernanceVote);
        tally.NodeType.Should().Be(ActionNodeType.GovernanceTally);

        // D20: the tally runs in a pure-DB node, needing no chain capability and no
        // custody — that is what lets governance ship alongside the KMS workstream.
        create.RequiresChainCapability.Should().BeFalse();
        vote.RequiresChainCapability.Should().BeFalse();
        tally.RequiresChainCapability.Should().BeFalse();
    }

    // ── proposal create ───────────────────────────────────────────────────────

    [Fact]
    public async Task ProposalCreate_RecordsDerivedUniquenessKey_AndDeclaredScope()
    {
        var store = new List<Holon>();
        var handler = new GovernanceProposalCreateNodeHandler(HolonWorld(store).Object, ClockAt(Now));

        var result = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceProposalCreate,
            new GovernanceProposalCreateNodeConfig
            {
                Name = "Adopt the new bylaws",
                Options = new List<string> { "yes", "no", "abstain" },
                ClosesAt = Now.AddDays(7),
                UniquenessScope = "Global",
            },
            Owner));

        var output = Output(result);
        var proposal = store.Single(h => h.AssetType == ConsumerGovernanceHolonTypes.ProposalAssetType);

        proposal.UserId.Should().Be(Owner);
        proposal.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.Status]
            .Should().Be(ConsumerGovernanceHolonTypes.StatusOpen);
        proposal.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessScope]
            .Should().Be(nameof(PersonUniquenessScope.Global));

        // The key is DERIVED from the proposal's own id — a tenant-authored key would
        // let two proposals share one uniqueness domain.
        proposal.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessOperationKey]
            .Should().Be(ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposal.Id));
        output.GetProperty("uniquenessOperationKey").GetString()
            .Should().Be(ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposal.Id));
        output.GetProperty("proposalId").GetString().Should().Be(proposal.Id.ToString());
    }

    /// <summary>
    /// G2. A proposal only its author can read is a proposal only its author can vote
    /// on: <c>HolonManager.CanRead</c> is owner-or-public and
    /// <c>GovernanceVote</c> reads the proposal as the RUNNER. Publication is what
    /// makes an electorate possible, so it is part of creating the notice.
    /// </summary>
    [Fact]
    public async Task ProposalCreate_PublishesTheProposal_SoOtherMembersCanReadIt()
    {
        var store = new List<Holon>();
        var handler = new GovernanceProposalCreateNodeHandler(HolonWorld(store).Object, ClockAt(Now));

        await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceProposalCreate,
            new GovernanceProposalCreateNodeConfig { Name = "Budget", Options = new List<string> { "yes", "no" } },
            Owner));

        var proposal = store.Single(h => h.AssetType == ConsumerGovernanceHolonTypes.ProposalAssetType);
        proposal.IsPublic.Should().BeTrue();
        CanRead(proposal, Stranger).Should().BeTrue("a member who is not the author has to be able to read the notice");
    }

    [Fact]
    public async Task ProposalCreate_RejectsFewerThanTwoOptions_Terminally()
    {
        var store = new List<Holon>();
        var handler = new GovernanceProposalCreateNodeHandler(HolonWorld(store).Object, ClockAt(Now));

        var result = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceProposalCreate,
            new GovernanceProposalCreateNodeConfig { Name = "One-horse race", Options = new List<string> { "yes" } },
            Owner));

        result.IsError.Should().BeTrue();
        result.Retriable.Should().BeFalse();
        store.Should().BeEmpty();
    }

    [Fact]
    public async Task ProposalCreate_RejectsNumericScope()
    {
        var store = new List<Holon>();
        var handler = new GovernanceProposalCreateNodeHandler(HolonWorld(store).Object, ClockAt(Now));

        // "1" is PersonUniquenessScope.Global by ordinal. Accepting it would let a
        // tenant ballot silently become a node-global one without spelling it out.
        var result = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceProposalCreate,
            new GovernanceProposalCreateNodeConfig
            {
                Name = "Sneaky",
                Options = new List<string> { "yes", "no" },
                UniquenessScope = "1",
            },
            Owner));

        result.IsError.Should().BeTrue();
        store.Should().BeEmpty();
    }

    [Fact]
    public async Task ProposalCreate_DeletesTheProposal_WhenTheUniquenessKeyCannotBeRecorded()
    {
        var store = new List<Holon>();
        var trace = new List<string>();
        var handler = new GovernanceProposalCreateNodeHandler(
            HolonWorld(store, trace, updateError: _ => "store unavailable").Object, ClockAt(Now));

        var result = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceProposalCreate,
            new GovernanceProposalCreateNodeConfig { Name = "Bylaws", Options = new List<string> { "yes", "no" } },
            Owner));

        // A proposal with no uniqueness key would be one no vote could trust; it must
        // not survive as a half-built ballot box.
        result.IsError.Should().BeTrue();
        trace.Should().Contain("delete");
        store.Should().BeEmpty();
    }

    // ── vote: the uniqueness consultation ─────────────────────────────────────

    [Fact]
    public async Task Vote_ClaimsUniqueness_BeforeWritingBallot()
    {
        var store = new List<Holon>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        var trace = new List<string>();
        var claimed = new List<PersonUniquenessRequirement>();
        var gate = Gate(claimed, trace: trace);

        var handler = new GovernanceVoteNodeHandler(
            HolonWorld(store, trace).Object, gate.Object, ClockAt(Now));

        var result = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = proposal.Id, Option = "yes" },
            Voter));

        result.IsError.Should().BeFalse(result.Message ?? string.Empty);
        gate.Verify(g => g.TryClaimAsync(
            It.IsAny<PersonUniquenessRequirement>(), Voter, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Order matters: a ballot written before the claim is a ballot the gate can no
        // longer refuse.
        trace.Should().ContainInOrder("claim", $"create:{ConsumerGovernanceHolonTypes.VoteAssetType}");
        claimed.Single().OperationKey
            .Should().Be(ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposal.Id));

        // D39: the chosen option rides ON the claim requirement, because the claim row
        // IS the vote of record. Drop it and the gate writes a claim with no decision,
        // the tally counts nobody, and every ballot silently evaporates.
        claimed.Single().Decision.Should().Be("yes");
    }

    /// <summary>
    /// G2. The governance primitive is inert unless a member who does NOT own the
    /// proposal can cast a ballot. Un-publish the proposal and the same vote dies on
    /// the owner-or-public read, which is exactly the defect this pins.
    /// </summary>
    [Fact]
    public async Task Vote_ByAMemberWhoDoesNotOwnTheProposal_IsAccepted_OnlyOnAPublishedProposal()
    {
        var store = new List<Holon>();
        var published = SeedProposal(store, Owner, new[] { "yes", "no" });
        var unpublished = SeedProposal(store, Owner, new[] { "yes", "no" }, isPublic: false);

        var handler = new GovernanceVoteNodeHandler(
            HolonWorld(store).Object, Gate(new List<PersonUniquenessRequirement>()).Object, ClockAt(Now));

        var accepted = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = published.Id, Option = "yes" },
            Voter));

        var refused = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = unpublished.Id, Option = "yes" },
            Stranger));

        accepted.IsError.Should().BeFalse(accepted.Message ?? string.Empty);
        refused.IsError.Should().BeTrue();

        var ballot = store.Single(h => h.AssetType == ConsumerGovernanceHolonTypes.VoteAssetType);
        ballot.UserId.Should().Be(Voter);
        // The ballot itself is NEVER published: Holon.UserId beside the choice is the
        // person-to-choice correlation D20 exists to prevent.
        ballot.IsPublic.Should().BeFalse();
    }

    [Fact]
    public async Task Vote_Refused_WhenGateSaysIneligible_AndNoBallotIsWritten()
    {
        var store = new List<Holon>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        var gate = Gate(new List<PersonUniquenessRequirement>(), eligible: false);

        var handler = new GovernanceVoteNodeHandler(HolonWorld(store).Object, gate.Object, ClockAt(Now));

        var result = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = proposal.Id, Option = "yes" },
            Voter));

        // Delete the TryClaimAsync consultation and this ballot appears: one human
        // holding N verified users would vote N times.
        result.IsError.Should().BeTrue();
        store.Should().NotContain(h => h.AssetType == ConsumerGovernanceHolonTypes.VoteAssetType);
    }

    [Fact]
    public async Task Vote_UsesTheProposalsScope_NotTheVotersChoice()
    {
        var store = new List<Holon>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" }, scope: "Global");
        var claimed = new List<PersonUniquenessRequirement>();

        var handler = new GovernanceVoteNodeHandler(
            HolonWorld(store).Object, Gate(claimed).Object, ClockAt(Now));

        await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = proposal.Id, Option = "yes" },
            Voter));

        claimed.Single().Scope.Should().Be(PersonUniquenessScope.Global);

        // Structural half of the same rule: the vote config offers no scope knob, so a
        // voter cannot pick a uniqueness domain nobody else claims in.
        typeof(GovernanceVoteNodeConfig).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(
                nameof(GovernanceVoteNodeConfig.ProposalHolonId),
                nameof(GovernanceVoteNodeConfig.Option));
    }

    /// <summary>
    /// D39 INVERTS the old compensation rule. The vote of record is committed the
    /// moment the gate writes the claim; the holon written afterwards is only the
    /// voter's receipt. A failed receipt write must therefore NOT release the claim —
    /// releasing would discard a decision the ledger has already recorded (and, on a
    /// re-vote, a valid earlier one) because a holon write failed.
    /// </summary>
    /// <remarks>
    /// Replaces <c>Vote_ReleasesTheClaim_WhenTheBallotWriteFails</c>, which was correct
    /// only while the ballot HOLON was the vote: then a held claim with no ballot
    /// behind it disenfranchised the human. Under D39 the claim IS the ballot, so the
    /// same release is data loss.
    /// </remarks>
    [Fact]
    public async Task Vote_KeepsTheCommittedClaim_WhenOnlyTheReceiptWriteFails()
    {
        var store = new List<Holon>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        var released = new List<PersonUniquenessRequirement>();
        var gate = Gate(new List<PersonUniquenessRequirement>(), released: released);

        var handler = new GovernanceVoteNodeHandler(
            HolonWorld(store, createError: _ => "store unavailable").Object, gate.Object, ClockAt(Now));

        var result = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = proposal.Id, Option = "yes" },
            Voter));

        // The vote STANDS: the ledger holds it and the caller is told which option.
        var output = Output(result);
        output.GetProperty("option").GetString().Should().Be("yes");

        // No receipt, and no compensation. A release here would erase a recorded vote.
        output.GetProperty("voteId").ValueKind.Should().Be(JsonValueKind.Null);
        released.Should().BeEmpty();
        store.Should().NotContain(h => h.AssetType == ConsumerGovernanceHolonTypes.VoteAssetType);
    }

    [Fact]
    public async Task Vote_ReceiptCarriesOnlyOptionAndCastAt_NeverAPersonKey()
    {
        var store = new List<Holon>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });

        var handler = new GovernanceVoteNodeHandler(
            HolonWorld(store).Object, Gate(new List<PersonUniquenessRequirement>()).Object, ClockAt(Now));

        await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = proposal.Id, Option = "no" },
            Voter));

        var ballot = store.Single(h => h.AssetType == ConsumerGovernanceHolonTypes.VoteAssetType);

        ballot.ParentHolonId.Should().Be(proposal.Id);
        ballot.UserId.Should().Be(Voter);
        ballot.Metadata.Keys.Should().BeEquivalentTo(
            ConsumerGovernanceHolonTypes.VoteMetadata.Option,
            ConsumerGovernanceHolonTypes.VoteMetadata.CastAt);

        // A vote child is consumer-readable through HolonQuery: a person key here
        // instantly reconstitutes the person-to-choice correlation.
        ballot.Metadata.Keys.Should().NotIntersectWith(ConsumerGovernanceHolonTypes.ForbiddenVoteMetadataKeys);
        ballot.TokenId.Should().BeNull();
    }

    [Fact]
    public async Task Vote_FailsClosed_WhenTheProposalsUniquenessKeyWasRewritten()
    {
        var store = new List<Holon>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        proposal.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessOperationKey] =
            "governance:proposal:something-else";
        var claimed = new List<PersonUniquenessRequirement>();

        var handler = new GovernanceVoteNodeHandler(
            HolonWorld(store).Object, Gate(claimed).Object, ClockAt(Now));

        var result = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = proposal.Id, Option = "yes" },
            Voter));

        result.IsError.Should().BeTrue();
        claimed.Should().BeEmpty();
        store.Should().NotContain(h => h.AssetType == ConsumerGovernanceHolonTypes.VoteAssetType);
    }

    [Fact]
    public async Task Vote_RejectsAnUndeclaredOption()
    {
        var store = new List<Holon>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });

        var handler = new GovernanceVoteNodeHandler(
            HolonWorld(store).Object, Gate(new List<PersonUniquenessRequirement>()).Object, ClockAt(Now));

        var result = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = proposal.Id, Option = "maybe" },
            Voter));

        result.IsError.Should().BeTrue();
        store.Should().NotContain(h => h.AssetType == ConsumerGovernanceHolonTypes.VoteAssetType);
    }

    /// <summary>
    /// D40. Voting again before the close is ALLOWED and replaces the earlier option:
    /// one row per person means the change is an update of that row, not a second
    /// ballot. The receipt mirrors it — one receipt per voter per proposal, refreshed
    /// in place — so nothing in the holarchy ever looks like two ballots from one human.
    /// </summary>
    /// <remarks>
    /// Replaces <c>Vote_RejectsASecondBallotFromTheSameUser</c>, which pinned the
    /// pre-D39 rule: with the outcome living in holons a re-vote meant two ballots and
    /// an ordering rule to pick between them, so the handler refused it outright. The
    /// ledger tally removes that cost. The COUNT side of the same rule is
    /// <c>Governance/GovernanceVoteOfRecordTests.Vote_CastAgainBeforeClose_ReplacesThePriorOption_WithNoDuplicateCounted</c>.
    /// </remarks>
    [Fact]
    public async Task Vote_CastAgain_RefreshesTheOneReceipt_RatherThanAppendingASecond()
    {
        var store = new List<Holon>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        var claimed = new List<PersonUniquenessRequirement>();

        var handler = new GovernanceVoteNodeHandler(
            HolonWorld(store).Object, Gate(claimed).Object, ClockAt(Now));

        var first = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = proposal.Id, Option = "yes" },
            Voter));

        var second = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = proposal.Id, Option = "no" },
            Voter));

        first.IsError.Should().BeFalse(first.Message ?? string.Empty);
        second.IsError.Should().BeFalse(second.Message ?? string.Empty);

        // The second vote re-claims the SAME uniqueness row, carrying the new decision.
        claimed.Should().HaveCount(2);
        claimed[1].OperationKey.Should().Be(claimed[0].OperationKey);
        claimed[1].Decision.Should().Be("no");

        var receipt = store.Single(h => h.AssetType == ConsumerGovernanceHolonTypes.VoteAssetType);
        receipt.UserId.Should().Be(Voter);
        receipt.Metadata[ConsumerGovernanceHolonTypes.VoteMetadata.Option].Should().Be("no");
        Output(second).GetProperty("voteId").GetString().Should().Be(receipt.Id.ToString());
    }

    [Fact]
    public async Task Vote_GivesTheSameAnswerForAnUnreadableProposalAndAMissingOne()
    {
        var store = new List<Holon>();
        var privateProposal = SeedProposal(store, Owner, new[] { "yes", "no" }, isPublic: false);

        var handler = new GovernanceVoteNodeHandler(
            HolonWorld(store).Object, Gate(new List<PersonUniquenessRequirement>()).Object, ClockAt(Now));

        var unreadable = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = privateProposal.Id, Option = "yes" },
            Stranger));

        var missing = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = Guid.NewGuid(), Option = "yes" },
            Stranger));

        // Two different sentences would make the vote node a proposal-existence oracle.
        unreadable.IsError.Should().BeTrue();
        missing.IsError.Should().BeTrue();
        unreadable.Message.Should().Be(missing.Message);
    }

    [Fact]
    public async Task Vote_RejectsABallotAfterTheProposalClosed()
    {
        var store = new List<Holon>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" }, closesAt: Now.AddHours(-1));

        var handler = new GovernanceVoteNodeHandler(
            HolonWorld(store).Object, Gate(new List<PersonUniquenessRequirement>()).Object, ClockAt(Now));

        var result = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = proposal.Id, Option = "yes" },
            Voter));

        result.IsError.Should().BeTrue();
        store.Should().NotContain(h => h.AssetType == ConsumerGovernanceHolonTypes.VoteAssetType);
    }

    // ── tally ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tally_CountsEveryDeclaredOption_AndMatchesTheCanonicalHash()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var closesAt = Now.AddDays(3);
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no", "abstain" }, closesAt: closesAt);
        SeedVoteOfRecord(claims, proposal.Id, "person-voter", Voter, "yes");
        SeedVoteOfRecord(claims, proposal.Id, "person-stranger", Stranger, "yes");
        SeedVoteOfRecord(claims, proposal.Id, "person-third", Guid.NewGuid(), "no");

        var output = Output(await TallyHandler(store, claims).HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner)));

        output.GetProperty("totalVotes").GetInt32().Should().Be(3);
        var counts = output.GetProperty("counts");
        counts.GetProperty("yes").GetInt32().Should().Be(2);
        counts.GetProperty("no").GetInt32().Should().Be(1);
        counts.GetProperty("abstain").GetInt32().Should().Be(0);

        var expected = GovernanceTallyHash.Compute(
            proposal.Id,
            closesAt.ToUniversalTime().ToString("O"),
            3,
            new Dictionary<string, int> { ["yes"] = 2, ["no"] = 1, ["abstain"] = 0 });
        output.GetProperty("tallyHash").GetString().Should().Be(expected);
    }

    /// <summary>
    /// Every declared option comes back, including the ones nobody chose: a count that
    /// silently omitted a zero would move the tally-hash preimage as votes arrive.
    /// </summary>
    [Fact]
    public async Task Tally_ListsEveryDeclaredOption_WhenNobodyVoted()
    {
        var store = new List<Holon>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });

        var output = Output(await TallyHandler(store, new List<PersonOperationClaim>()).HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner)));

        output.GetProperty("totalVotes").GetInt32().Should().Be(0);
        output.GetProperty("counts").GetProperty("yes").GetInt32().Should().Be(0);
        output.GetProperty("counts").GetProperty("no").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Tally_OutputCarriesNoVoterIdentity()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        SeedVoteOfRecord(claims, proposal.Id, "person-voter", Voter, "yes");
        SeedVoteOfRecord(claims, proposal.Id, "person-stranger", Stranger, "no");

        var result = await TallyHandler(store, claims).HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner));

        // D39 puts person_operation_claim.decision_option BESIDE person_key, so the
        // node operator — and only the node operator — can read person-to-choice. That
        // is exactly why only counts leave this node, and only counts enter the hash.
        result.Output.Should().NotContain(Voter.ToString()).And.NotContain(Stranger.ToString());
        result.Output.Should().NotContain("person-voter").And.NotContain("person-stranger");
        GovernanceTallyHash.Preimage(proposal.Id, null, 2, new Dictionary<string, int> { ["yes"] = 1, ["no"] = 1 })
            .Should().NotContain(Voter.ToString()).And.NotContain(Stranger.ToString());
    }

    /// <summary>
    /// The forged-ballot property, restated as D39 makes it true: a
    /// <c>GovernanceVote</c> holon written straight through <c>POST /api/holon</c>
    /// counts for nothing BY CONSTRUCTION, because the tally reads no ballot holon at
    /// all. That is strictly stronger than what this test used to assert — a
    /// claim-JOINED ballot count, which was considered and REJECTED: the join still let
    /// an attacker-written holon decide WHICH option a claim was spent on, and it
    /// stayed safe only while nobody dropped it.
    /// </summary>
    /// <remarks>
    /// The STRUCTURAL half is asserted here; the behavioural half is
    /// <c>Governance/GovernanceVoteOfRecordTests.Tally_CountsNoBallotHolonWrittenOutsideTheVoteNode</c>
    /// (the 500-holon attack, through the real <c>HolonManager</c>). Neither alone is
    /// the guarantee: the guard catches a tally that REGAINS a way to read ballots, and
    /// the attack test catches one that keeps the ledger dependency and stops
    /// reflecting it.
    /// </remarks>
    [Fact]
    public async Task Tally_CountsNoForgedBallotHolon_BecauseItReadsNoBallotHolonAtAll()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });

        SeedVoteOfRecord(claims, proposal.Id, "person-voter", Voter, "yes");

        // A verified user who never went through GovernanceVote and holds no claim,
        // plus the claim holder flooding their own proposal with rewritten choices.
        SeedBallot(store, proposal.Id, Stranger, "no");
        SeedBallot(store, proposal.Id, Stranger, "no");
        for (var extra = 1; extra <= 20; extra++)
            SeedBallot(store, proposal.Id, Voter, "no", Now.AddMinutes(extra));

        var output = Output(await TallyHandler(store, claims).HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner)));

        output.GetProperty("totalVotes").GetInt32().Should().Be(1);
        output.GetProperty("counts").GetProperty("yes").GetInt32().Should().Be(1);
        output.GetProperty("counts").GetProperty("no").GetInt32().Should().Be(0);

        // Executable form of "by construction": the handler holds no ballot-reading
        // surface, so there is no join left for a later change to drop.
        GovernanceVoteOfRecordGuard.Violations(typeof(GovernanceTallyNodeHandler))
            .Should().BeEmpty();
    }

    /// <summary>
    /// The count must not move with the caller. The ledger read is keyed by (scope,
    /// operation) and carries no caller term at all, so a runner who cast no vote — and
    /// whose consumer-scoped read would show them nobody else's receipt — still gets
    /// the honest total, and the certified digest is the digest of that honest count.
    /// </summary>
    [Fact]
    public async Task Tally_CountsEveryVotersDecision_NotJustTheRunners()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        SeedVoteOfRecord(claims, proposal.Id, "person-voter", Voter, "yes");
        SeedVoteOfRecord(claims, proposal.Id, "person-stranger", Stranger, "no");

        // Owner holds no claim on this proposal and owns none of the receipts.
        var output = Output(await TallyHandler(store, claims).HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner)));

        output.GetProperty("totalVotes").GetInt32().Should().Be(2);
        output.GetProperty("counts").GetProperty("yes").GetInt32().Should().Be(1);
        output.GetProperty("counts").GetProperty("no").GetInt32().Should().Be(1);

        output.GetProperty("tallyHash").GetString().Should().Be(GovernanceTallyHash.Compute(
            proposal.Id, null, 2, new Dictionary<string, int> { ["yes"] = 1, ["no"] = 1 }));
    }

    /// <summary>
    /// A released claim is a compensated one and no longer a vote. The store excludes
    /// released rows by contract AND the tally re-checks <c>ReleasedAt</c> on every row
    /// it is handed, so a store that forgets its own filter cannot turn a withdrawn
    /// vote back into a counted one.
    /// </summary>
    [Fact]
    public async Task Tally_IgnoresAReleasedClaim_EvenFromALedgerThatForgetsToFilter()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        SeedVoteOfRecord(claims, proposal.Id, "person-voter", Voter, "yes", released: true);

        var context = Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner);

        Output(await TallyHandler(store, claims).HandleAsync(context))
            .GetProperty("totalVotes").GetInt32().Should().Be(0);

        var forgetfulLedger = new GovernanceTallyNodeHandler(
            HolonWorld(store).Object, LedgerOf(claims, includeReleased: true).Object, ClockAt(Now));

        Output(await forgetfulLedger.HandleAsync(context))
            .GetProperty("totalVotes").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// The uniqueness domain is the PROPOSAL's electorate, never the caller's. A claim
    /// taken under another tenant is not evidence in this ballot, and counting it would
    /// let a second tenant's members vote in this one's proposal.
    /// </summary>
    [Fact]
    public async Task Tally_DoesNotCountAClaimMadeUnderAnotherTenant()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var otherTenant = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var thisTenant = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" }, electorateTenant: thisTenant);

        // Same proposal, two uniqueness domains. Only the electorate's own domain counts.
        SeedVoteOfRecord(claims, proposal.Id, "person-outsider", Voter, "yes", tenantId: otherTenant);
        SeedVoteOfRecord(claims, proposal.Id, "person-member", Stranger, "no", tenantId: thisTenant);

        var output = Output(await TallyHandler(store, claims).HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner,
            thisTenant)));

        output.GetProperty("totalVotes").GetInt32().Should().Be(1);
        output.GetProperty("counts").GetProperty("no").GetInt32().Should().Be(1);
        output.GetProperty("counts").GetProperty("yes").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// AC5 for the COUNT. A proposal is published so its electorate can read the
    /// notice, and <c>HolonManager.CanRead</c> has no tenant term — so the tally
    /// refuses a caller outside the recorded electorate with the SAME sentence it uses
    /// for a proposal that does not exist. Two different sentences would make the tally
    /// a proposal-existence oracle across tenants.
    /// </summary>
    [Fact]
    public async Task Tally_GivesTheSameAnswerOutsideTheElectorateAndForAMissingProposal()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var electorate = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var foreignTenant = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" }, electorateTenant: electorate);
        SeedVoteOfRecord(claims, proposal.Id, "person-member", Voter, "yes", tenantId: electorate);

        var handler = TallyHandler(store, claims);

        var foreign = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Stranger,
            foreignTenant));

        var missing = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = Guid.NewGuid() },
            Stranger,
            foreignTenant));

        foreign.IsError.Should().BeTrue();
        foreign.Output.Should().BeNull();
        foreign.Message.Should().Be(missing.Message);
    }

    /// <summary>
    /// Fail closed, never fail quiet: a ledger that cannot be read must not publish a
    /// count of zero under a correct-looking tallyHash.
    /// </summary>
    [Fact]
    public async Task Tally_FailsClosed_WhenTheClaimLedgerCannotBeRead()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        SeedVoteOfRecord(claims, proposal.Id, "person-voter", Voter, "yes");

        var result = await TallyHandler(store, claims, ledgerReadError: "ledger down").HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner));

        result.IsError.Should().BeTrue();
        result.Output.Should().BeNull();
    }

    /// <summary>
    /// The tally reads the uniqueness domain from the proposal exactly as the vote
    /// does, including the derived-key equality check, so a rewritten key cannot aim
    /// the count at a domain nobody claimed in.
    /// </summary>
    [Fact]
    public async Task Tally_FailsClosed_WhenTheProposalsUniquenessKeyWasRewritten()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        SeedVoteOfRecord(claims, proposal.Id, "person-voter", Voter, "yes");
        proposal.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessOperationKey] =
            "governance:proposal:somebody-elses-domain";

        var result = await TallyHandler(store, claims).HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner));

        result.IsError.Should().BeTrue();
        result.Output.Should().BeNull();
    }

    // ── the hash itself ───────────────────────────────────────────────────────

    [Fact]
    public void TallyHash_IsOrderStable()
    {
        var proposalId = Guid.NewGuid();
        var ascending = new Dictionary<string, int> { ["abstain"] = 0, ["no"] = 1, ["yes"] = 2 };
        var shuffled = new Dictionary<string, int> { ["yes"] = 2, ["abstain"] = 0, ["no"] = 1 };

        GovernanceTallyHash.Compute(proposalId, "2026-09-06T00:00:00.0000000+00:00", 3, ascending)
            .Should().Be(GovernanceTallyHash.Compute(proposalId, "2026-09-06T00:00:00.0000000+00:00", 3, shuffled));
    }

    [Fact]
    public void TallyHash_LengthPrefixedOptions_CannotForgeTheSameDigest()
    {
        var proposalId = Guid.NewGuid();

        // Without the length prefix, an option that embeds the separators could serialize
        // to the same bytes as two separate options.
        var honest = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
        var forged = new Dictionary<string, int> { ["a=1\n1:b"] = 2 };

        GovernanceTallyHash.Compute(proposalId, null, 3, honest)
            .Should().NotBe(GovernanceTallyHash.Compute(proposalId, null, 3, forged));
    }

    [Fact]
    public void TallyHash_ChangesWithTheCloseTime()
    {
        var proposalId = Guid.NewGuid();
        var counts = new Dictionary<string, int> { ["yes"] = 1 };

        GovernanceTallyHash.Compute(proposalId, "2026-09-06T00:00:00.0000000+00:00", 1, counts)
            .Should().NotBe(GovernanceTallyHash.Compute(proposalId, "2026-09-07T00:00:00.0000000+00:00", 1, counts));
    }

    // ── registry seed ─────────────────────────────────────────────────────────

    [Fact]
    public void SeedRegistrations_ConstrainBothAssetTypes_WithoutRequiringTheDerivedKey()
    {
        var seeds = ConsumerGovernanceHolonTypes.SeedRegistrations(Now);

        seeds.Select(s => s.AssetType).Should().BeEquivalentTo(
            ConsumerGovernanceHolonTypes.ProposalAssetType,
            ConsumerGovernanceHolonTypes.VoteAssetType);

        var proposalSeed = seeds.Single(s => s.AssetType == ConsumerGovernanceHolonTypes.ProposalAssetType);
        proposalSeed.RequiredMetadataFields.Should().Contain(ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessScope);

        // uniquenessOperationKey is derived from the proposal's own id, so it is
        // recorded a beat after creation and cannot be required at create time.
        proposalSeed.RequiredMetadataFields
            .Should().NotContain(ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessOperationKey);

        var voteSeed = seeds.Single(s => s.AssetType == ConsumerGovernanceHolonTypes.VoteAssetType);
        voteSeed.RequiredMetadataFields.Should().BeEquivalentTo(
            ConsumerGovernanceHolonTypes.VoteMetadata.Option,
            ConsumerGovernanceHolonTypes.VoteMetadata.CastAt);
    }
}
