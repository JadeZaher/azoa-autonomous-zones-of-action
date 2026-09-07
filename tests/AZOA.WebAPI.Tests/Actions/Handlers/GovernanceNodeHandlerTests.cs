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
using KycIdentity = AZOA.WebAPI.Persistence.SurrealDb.Models.KycIdentity;
using PersonOperationClaim = AZOA.WebAPI.Persistence.SurrealDb.Models.PersonOperationClaim;

namespace AZOA.WebAPI.Tests.Actions.Handlers;

/// <summary>
/// Dispatch tests for the D20 consumer-governance nodes: proposal create, vote, tally.
/// </summary>
/// <remarks>
/// The load-bearing assertions are the Sybil ones. <c>GovernanceVote</c> MUST consult
/// <see cref="IPersonUniquenessGate"/> before a ballot is written, in the uniqueness
/// domain the PROPOSAL declared — remove the consultation and
/// <c>Vote_Refused_WhenGateSaysIneligible_AndNoBallotIsWritten</c> plus
/// <c>Vote_ClaimsUniqueness_BeforeWritingBallot</c> both fail. The other load-bearing
/// assertion is that a ballot carries the choice and the time and NOTHING else: a
/// person key on a consumer-readable vote holon reconstitutes the person-to-choice
/// correlation the whole design exists to prevent.
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
    /// A proposal exactly as <c>GovernanceProposalCreate</c> leaves it.
    /// <paramref name="isPublic"/> models the electorate case: a private proposal is
    /// readable only by its author (see Services/Actions/AGENTS.md §governance).
    /// </summary>
    private static Holon SeedProposal(
        List<Holon> store,
        Guid ownerId,
        string[] options,
        string scope = "Tenant",
        string status = "Open",
        DateTimeOffset? closesAt = null,
        bool isPublic = true)
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

    // -- the tally's privileged world: ballots plus the claim ledger behind them --

    /// <summary>
    /// The UNSCOPED store read the tally's <c>GovernanceBallotRoll</c> performs. It
    /// deliberately ignores ownership: that is the whole reason the roll returns
    /// counts and never rows.
    /// </summary>
    private static Mock<IHolonStore> HolonStoreOver(List<Holon> store, string? queryError = null)
    {
        var mock = new Mock<IHolonStore>();
        mock.Setup(s => s.QueryAsync(It.IsAny<HolonQueryRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HolonQueryRequest? query, CancellationToken _) =>
                queryError is not null
                    ? new AZOAResult<IEnumerable<IHolon>> { IsError = true, Message = queryError }
                    : new AZOAResult<IEnumerable<IHolon>>
                    {
                        Result = store
                            .Where(h => query is null || query.ParentHolonId is null || h.ParentHolonId == query.ParentHolonId)
                            .Cast<IHolon>()
                            .ToList(),
                    });
        return mock;
    }

    /// <summary>Acting user to the person behind them; an unlisted user has no binding at all.</summary>
    private static Mock<IKycIdentityStore> IdentitiesFor(
        Dictionary<Guid, string> personKeyByUser, string? readError = null)
    {
        var mock = new Mock<IKycIdentityStore>();
        mock.Setup(s => s.GetByUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string scope, string userKey, CancellationToken _) =>
            {
                if (readError is not null)
                    return new AZOAResult<KycIdentity?> { IsError = true, Message = readError };

                var personKey = personKeyByUser
                    .Where(kv => kv.Key.ToString("N") == userKey)
                    .Select(kv => kv.Value)
                    .FirstOrDefault();

                return personKey is null
                    ? new AZOAResult<KycIdentity?> { Result = null }
                    : new AZOAResult<KycIdentity?>
                    {
                        Result = new KycIdentity
                        {
                            TenantScope = scope,
                            PersonKey = personKey,
                            GlobalPersonKey = "global:" + personKey,
                        },
                    };
            });
        return mock;
    }

    /// <summary>The node-operator-only claim ledger, keyed exactly as the gate writes it.</summary>
    private static Mock<IPersonOperationClaimStore> ClaimsFor(
        List<PersonOperationClaim> claims, string? readError = null)
    {
        var mock = new Mock<IPersonOperationClaimStore>();
        mock.Setup(s => s.GetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string scope, string operationKey, string personKey, CancellationToken _) =>
                readError is not null
                    ? new AZOAResult<PersonOperationClaim?> { IsError = true, Message = readError }
                    : new AZOAResult<PersonOperationClaim?>
                    {
                        Result = claims.FirstOrDefault(c =>
                            c.UniquenessScope == scope
                            && c.OperationKey == operationKey
                            && c.PersonKey == personKey),
                    });
        return mock;
    }

    /// <summary>The ledger row the vote handler's TryClaimAsync would have left behind.</summary>
    private static PersonOperationClaim SeedClaim(
        List<PersonOperationClaim> claims,
        Guid proposalId,
        string personKey,
        Guid holder,
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
        };
        claims.Add(claim);
        return claim;
    }

    /// <summary>A member who voted THROUGH the handler: a live claim plus one ballot.</summary>
    private static Holon SeedClaimBackedBallot(
        List<Holon> store,
        List<PersonOperationClaim> claims,
        Dictionary<Guid, string> personKeyByUser,
        Guid proposalId,
        Guid voter,
        string option,
        DateTimeOffset? castAt = null)
    {
        if (!personKeyByUser.TryGetValue(voter, out var personKey))
        {
            personKey = $"person-{voter:N}";
            personKeyByUser[voter] = personKey;
        }

        var operationKey = ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposalId);
        if (!claims.Any(c => c.PersonKey == personKey && c.OperationKey == operationKey))
            SeedClaim(claims, proposalId, personKey, voter);

        return SeedBallot(store, proposalId, voter, option, castAt);
    }

    private static GovernanceTallyNodeHandler TallyHandler(
        List<Holon> store,
        List<PersonOperationClaim> claims,
        Dictionary<Guid, string> personKeyByUser,
        string? holonQueryError = null,
        string? identityReadError = null,
        string? claimReadError = null) =>
        new(HolonWorld(store).Object,
            HolonStoreOver(store, holonQueryError).Object,
            IdentitiesFor(personKeyByUser, identityReadError).Object,
            ClaimsFor(claims, claimReadError).Object,
            ClockAt(Now));

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
        IActionNodeHandler tally = TallyHandler(
            store, new List<PersonOperationClaim>(), new Dictionary<Guid, string>());

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

    [Fact]
    public async Task Vote_ReleasesTheClaim_WhenTheBallotWriteFails()
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

        // A held claim with no ballot behind it disenfranchises this human for the rest
        // of the proposal.
        result.IsError.Should().BeTrue();
        released.Single().OperationKey
            .Should().Be(ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposal.Id));
    }

    [Fact]
    public async Task Vote_BallotCarriesOnlyOptionAndCastAt_NeverAPersonKey()
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

    [Fact]
    public async Task Vote_RejectsASecondBallotFromTheSameUser()
    {
        var store = new List<Holon>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        SeedBallot(store, proposal.Id, Voter, "yes");
        var claimed = new List<PersonUniquenessRequirement>();

        var handler = new GovernanceVoteNodeHandler(
            HolonWorld(store).Object, Gate(claimed).Object, ClockAt(Now));

        var result = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = proposal.Id, Option = "no" },
            Voter));

        // One ballot per USER is answered locally; one ballot per HUMAN is the gate's
        // job, so this path must not even spend a claim.
        result.IsError.Should().BeTrue();
        claimed.Should().BeEmpty();
        store.Count(h => h.AssetType == ConsumerGovernanceHolonTypes.VoteAssetType).Should().Be(1);
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
        var people = new Dictionary<Guid, string>();
        var closesAt = Now.AddDays(3);
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no", "abstain" }, closesAt: closesAt);
        SeedClaimBackedBallot(store, claims, people, proposal.Id, Voter, "yes");
        SeedClaimBackedBallot(store, claims, people, proposal.Id, Stranger, "yes");
        SeedClaimBackedBallot(store, claims, people, proposal.Id, Guid.NewGuid(), "no");

        var handler = TallyHandler(store, claims, people);

        var output = Output(await handler.HandleAsync(Ctx(
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

    [Fact]
    public async Task Tally_ListsEveryDeclaredOption_WhenNobodyVoted()
    {
        var store = new List<Holon>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });

        var handler = TallyHandler(store, new List<PersonOperationClaim>(), new Dictionary<Guid, string>());

        var output = Output(await handler.HandleAsync(Ctx(
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
        var people = new Dictionary<Guid, string>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        SeedClaimBackedBallot(store, claims, people, proposal.Id, Voter, "yes");
        SeedClaimBackedBallot(store, claims, people, proposal.Id, Stranger, "no");

        var handler = TallyHandler(store, claims, people);

        var result = await handler.HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner));

        // D20: anchoring ballots would make every member's choice permanently public.
        // Only counts leave this node, and only counts enter the hash preimage.
        result.Output.Should().NotContain(Voter.ToString()).And.NotContain(Stranger.ToString());
        GovernanceTallyHash.Preimage(proposal.Id, null, 2, new Dictionary<string, int> { ["yes"] = 1, ["no"] = 1 })
            .Should().NotContain(Voter.ToString()).And.NotContain(Stranger.ToString());
    }

    /// <summary>
    /// G1. The attack that works without this: one verified user POSTs
    /// /api/holon five hundred times with
    /// {"assetType":"GovernanceVote","parentHolonId":"..."} — no handler, no claim,
    /// and a child-holon count happily certifies all five hundred. Delete the claim
    /// consultation in <c>GovernanceBallotRoll</c> and this test reports no=3.
    /// </summary>
    [Fact]
    public async Task Tally_CountsOnlyClaimBackedBallots_AndIgnoresForgedOnes()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var people = new Dictionary<Guid, string>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });

        SeedClaimBackedBallot(store, claims, people, proposal.Id, Voter, "yes");

        // The forger is a real, verified user (they have an identity binding) who
        // simply never went through GovernanceVote, so they hold no claim.
        people[Stranger] = "person-stranger";
        SeedBallot(store, proposal.Id, Stranger, "no");
        SeedBallot(store, proposal.Id, Stranger, "no");
        SeedBallot(store, proposal.Id, Stranger, "no");

        var output = Output(await TallyHandler(store, claims, people).HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner)));

        output.GetProperty("totalVotes").GetInt32().Should().Be(1);
        output.GetProperty("counts").GetProperty("yes").GetInt32().Should().Be(1);
        output.GetProperty("counts").GetProperty("no").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// G1. A claim holder who then floods the proposal with hand-written ballots
    /// still counts once: each claim is spent at most once.
    /// </summary>
    [Fact]
    public async Task Tally_SpendsEachClaimOnce_NoMatterHowManyBallotsOneUserWrites()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var people = new Dictionary<Guid, string>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });

        SeedClaimBackedBallot(store, claims, people, proposal.Id, Voter, "yes", Now);
        for (var extra = 1; extra <= 20; extra++)
            SeedBallot(store, proposal.Id, Voter, "no", Now.AddMinutes(extra));

        var output = Output(await TallyHandler(store, claims, people).HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner)));

        // The earliest ballot is the one the claim pays for, so a re-tally is stable.
        output.GetProperty("totalVotes").GetInt32().Should().Be(1);
        output.GetProperty("counts").GetProperty("yes").GetInt32().Should().Be(1);
        output.GetProperty("counts").GetProperty("no").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// G3. Ballots are private and each is owned by its own voter, so a
    /// runner-scoped read shows the runner exactly one ballot: their own. Here the
    /// runner has cast none at all, and the honest answer is still two.
    /// </summary>
    [Fact]
    public async Task Tally_CountsEveryVotersBallot_NotJustTheRunners()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var people = new Dictionary<Guid, string>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        SeedClaimBackedBallot(store, claims, people, proposal.Id, Voter, "yes");
        SeedClaimBackedBallot(store, claims, people, proposal.Id, Stranger, "no");

        var output = Output(await TallyHandler(store, claims, people).HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner)));

        output.GetProperty("totalVotes").GetInt32().Should().Be(2);
        output.GetProperty("counts").GetProperty("yes").GetInt32().Should().Be(1);
        output.GetProperty("counts").GetProperty("no").GetInt32().Should().Be(1);

        // The certified digest has to be the digest of the honest count, not of the
        // runner-visible slice.
        output.GetProperty("tallyHash").GetString().Should().Be(GovernanceTallyHash.Compute(
            proposal.Id, null, 2, new Dictionary<string, int> { ["yes"] = 1, ["no"] = 1 }));
    }

    /// <summary>G1. A released claim is a compensated one: it no longer backs a ballot.</summary>
    [Fact]
    public async Task Tally_IgnoresABallotBackedByAReleasedClaim()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var people = new Dictionary<Guid, string> { [Voter] = "person-voter" };
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        SeedClaim(claims, proposal.Id, "person-voter", Voter, released: true);
        SeedBallot(store, proposal.Id, Voter, "yes");

        var output = Output(await TallyHandler(store, claims, people).HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner)));

        output.GetProperty("totalVotes").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// D23. Two users, ONE human: the uniqueness ledger holds a single row, so only
    /// the user that actually holds it votes. This is the Sybil case the whole gate
    /// exists for, and a per-user check alone would count both.
    /// </summary>
    [Fact]
    public async Task Tally_CountsOneBallotPerHUMAN_WhenOnePersonHoldsTwoUsers()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var secondUser = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var people = new Dictionary<Guid, string>
        {
            [Voter] = "one-and-the-same-person",
            [secondUser] = "one-and-the-same-person",
        };
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        SeedClaim(claims, proposal.Id, "one-and-the-same-person", Voter);
        SeedBallot(store, proposal.Id, Voter, "yes", Now);
        SeedBallot(store, proposal.Id, secondUser, "no", Now.AddMinutes(1));

        var output = Output(await TallyHandler(store, claims, people).HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner)));

        output.GetProperty("totalVotes").GetInt32().Should().Be(1);
        output.GetProperty("counts").GetProperty("yes").GetInt32().Should().Be(1);
        output.GetProperty("counts").GetProperty("no").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// A Tenant-scoped proposal counts the claims of its own uniqueness domain. A
    /// claim made under another tenant is not evidence here, and silently counting it
    /// would let a second tenant's members vote in this one's ballot.
    /// </summary>
    [Fact]
    public async Task Tally_DoesNotCountAClaimMadeUnderAnotherTenant()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var people = new Dictionary<Guid, string> { [Voter] = "person-voter" };
        var otherTenant = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var thisTenant = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        SeedClaim(claims, proposal.Id, "person-voter", Voter, tenantId: otherTenant);
        SeedBallot(store, proposal.Id, Voter, "yes");

        var output = Output(await TallyHandler(store, claims, people).HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner,
            thisTenant)));

        output.GetProperty("totalVotes").GetInt32().Should().Be(0);
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
        var people = new Dictionary<Guid, string>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        SeedClaimBackedBallot(store, claims, people, proposal.Id, Voter, "yes");

        var result = await TallyHandler(store, claims, people, claimReadError: "ledger down").HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner));

        result.IsError.Should().BeTrue();
        result.Output.Should().BeNull();
    }

    /// <summary>Same rule for the identity bindings the claim lookup goes through.</summary>
    [Fact]
    public async Task Tally_FailsClosed_WhenTheIdentityBindingsCannotBeRead()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var people = new Dictionary<Guid, string>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        SeedClaimBackedBallot(store, claims, people, proposal.Id, Voter, "yes");

        var result = await TallyHandler(store, claims, people, identityReadError: "identity store down").HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner));

        result.IsError.Should().BeTrue();
    }

    /// <summary>
    /// The tally reads the uniqueness domain from the proposal exactly as the vote
    /// does, so a rewritten key cannot aim the count at a domain nobody claimed in.
    /// </summary>
    [Fact]
    public async Task Tally_FailsClosed_WhenTheProposalsUniquenessKeyWasRewritten()
    {
        var store = new List<Holon>();
        var claims = new List<PersonOperationClaim>();
        var people = new Dictionary<Guid, string>();
        var proposal = SeedProposal(store, Owner, new[] { "yes", "no" });
        SeedClaimBackedBallot(store, claims, people, proposal.Id, Voter, "yes");
        proposal.Metadata[ConsumerGovernanceHolonTypes.ProposalMetadata.UniquenessOperationKey] =
            "governance:proposal:somebody-elses-domain";

        var result = await TallyHandler(store, claims, people).HandleAsync(Ctx(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposal.Id },
            Owner));

        result.IsError.Should().BeTrue();
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
