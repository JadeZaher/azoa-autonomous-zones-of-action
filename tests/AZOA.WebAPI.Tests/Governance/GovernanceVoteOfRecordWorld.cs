// SPDX-License-Identifier: UNLICENSED

using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Kyc;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;
// Aliased, never imported wholesale: AZOA.WebAPI.Persistence.SurrealDb.Models also
// declares a Holon, and importing that namespace makes every Holon here ambiguous.
using KycIdentity = AZOA.WebAPI.Persistence.SurrealDb.Models.KycIdentity;
using PersonOperationClaim = AZOA.WebAPI.Persistence.SurrealDb.Models.PersonOperationClaim;

namespace AZOA.WebAPI.Tests.Governance;

/// <summary>Clock the whole world shares, so a test can step past a proposal's close.</summary>
internal sealed class SteppableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>In-memory <see cref="IHolonStore"/> the REAL <see cref="HolonManager"/> runs on.</summary>
/// <remarks>See <c>tests/AZOA.WebAPI.Tests/Governance/AGENTS.md</c> §real-manager.</remarks>
internal sealed class InMemoryHolonStore : IHolonStore
{
    private readonly List<IHolon> _holons = new();

    public IReadOnlyList<IHolon> All => _holons;

    public Task<AZOAResult<IHolon>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var holon = _holons.FirstOrDefault(h => h.Id == id);
        return Task.FromResult(holon is null
            ? new AZOAResult<IHolon> { IsError = true, Message = "Holon not found." }
            : new AZOAResult<IHolon> { Result = holon });
    }

    public Task<AZOAResult<IEnumerable<IHolon>>> QueryAsync(HolonQueryRequest? query = null, CancellationToken ct = default)
    {
        IEnumerable<IHolon> rows = _holons;
        if (query?.ParentHolonId is { } parent)
            rows = rows.Where(h => h.ParentHolonId == parent);
        if (query?.AssetType is { } assetType)
            rows = rows.Where(h => string.Equals(h.AssetType, assetType, StringComparison.Ordinal));
        if (query?.UserId is { } userId)
            rows = rows.Where(h => h.UserId == userId);
        return Task.FromResult(new AZOAResult<IEnumerable<IHolon>> { Result = rows.ToList() });
    }

    public Task<AZOAResult<IHolon>> UpsertAsync(IHolon holon, CancellationToken ct = default)
    {
        _holons.RemoveAll(h => h.Id == holon.Id);
        _holons.Add(holon);
        return Task.FromResult(new AZOAResult<IHolon> { Result = holon });
    }

    public Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(new AZOAResult<bool> { Result = _holons.RemoveAll(h => h.Id == id) > 0 });

    // NFT settlement reservations are not part of the governance surface. Throwing
    // rather than returning a cheerful `true` keeps a future test from believing a
    // reservation happened here.
    public Task<AZOAResult<bool>> TryReserveNftTransferAsync(
        Guid holonId, Guid sourceUserId, Guid targetUserId, string settlementKey, CancellationToken ct = default)
        => throw new NotSupportedException("Governance tests do not exercise NFT settlement reservations.");

    public Task<AZOAResult<bool>> FinalizeReservedNftTransferAsync(
        Guid holonId, Guid sourceUserId, Guid targetUserId, string settlementKey, CancellationToken ct = default)
        => throw new NotSupportedException("Governance tests do not exercise NFT settlement reservations.");

    public Task<AZOAResult<bool>> ReleaseNftTransferReservationAsync(
        Guid holonId, Guid sourceUserId, string settlementKey, CancellationToken ct = default)
        => throw new NotSupportedException("Governance tests do not exercise NFT settlement reservations.");
}

/// <summary>The node-operator-only person-to-user binding, keyed exactly as the gate writes it.</summary>
internal sealed class InMemoryKycIdentityStore : IKycIdentityStore
{
    private readonly Dictionary<(string TenantScope, string UserKey), KycIdentity> _bindings = new();

    /// <summary>Registers a verified human: one user, one person key, inside one tenant scope.</summary>
    public void Bind(string tenantScope, Guid userId, string personKey) =>
        _bindings[(tenantScope, userId.ToString("N"))] = new KycIdentity
        {
            TenantScope = tenantScope,
            PersonKey = personKey,
            // Same derivation the tally's roll and the gate both assume.
            GlobalPersonKey = "global:" + personKey,
        };

    public Task<AZOAResult<KycIdentity?>> GetByUserAsync(string tenantScope, string userId, CancellationToken ct = default)
        => Task.FromResult(new AZOAResult<KycIdentity?>
        {
            Result = _bindings.TryGetValue((tenantScope, userId), out var identity) ? identity : null,
        });

    public Task<AZOAResult<KycIdentity>> BindUserAsync(KycIdentity candidate, CancellationToken ct = default)
        => throw new NotSupportedException("Bindings are seeded directly in these tests.");

    public Task<AZOAResult<KycIdentity>> GetByPersonKeyAsync(string tenantScope, string personKey, CancellationToken ct = default)
        => throw new NotSupportedException("Unused by the governance surface.");

    public Task<AZOAResult<IReadOnlyList<KycIdentity>>> GetByGlobalPersonKeyAsync(string globalPersonKey, CancellationToken ct = default)
        => throw new NotSupportedException("Unused by the governance surface.");

    public Task<AZOAResult<IReadOnlyList<KycIdentity>>> GetErasureDueAsync(DateTimeOffset asOf, int limit, CancellationToken ct = default)
        => throw new NotSupportedException("Unused by the governance surface.");

    public Task<AZOAResult<KycIdentity>> EraseAsync(string recordId, string reason, DateTimeOffset erasedAt, CancellationToken ct = default)
        => throw new NotSupportedException("Unused by the governance surface.");
}

/// <summary>
/// The per-operation Sybil ledger. A CONCRETE fake rather than a Moq mock on purpose:
/// when D39 adds the read the tally needs (the option carried on the claim, and a
/// list-by-operation), a mock would auto-implement it and quietly answer "nothing",
/// turning a missing implementation into a green test.
/// </summary>
internal sealed class InMemoryPersonOperationClaimLedger : IPersonOperationClaimStore
{
    private readonly List<PersonOperationClaim> _rows = new();

    public IReadOnlyList<PersonOperationClaim> Rows => _rows;

    /// <summary>
    /// Winner decided INSIDE the one write and read back by the caller — never by a
    /// preceding read, which two concurrent claimants would both pass.
    /// </summary>
    public Task<AZOAResult<PersonOperationClaim>> TryClaimAsync(PersonOperationClaim candidate, CancellationToken ct = default)
    {
        var existing = _rows.FirstOrDefault(r => string.Equals(r.Id, candidate.Id, StringComparison.Ordinal));
        if (existing is null)
        {
            _rows.Add(candidate);
            return Task.FromResult(new AZOAResult<PersonOperationClaim> { Result = candidate });
        }

                var ours = string.Equals(existing.UserId, candidate.UserId, StringComparison.Ordinal);
        var unheld = string.IsNullOrEmpty(existing.UserId) || existing.ReleasedAt is not null;

        if (unheld)
        {
            existing.UserId = candidate.UserId;
            existing.TenantId = candidate.TenantId;
            existing.ClaimedAt = candidate.ClaimedAt;
            existing.ReleasedAt = null;
        }

        // D40: last write wins, IN PLACE, while the window is open. The close is
        // pinned write-once so re-opening a proposal cannot re-open a decided row.
        if ((unheld || ours) && !string.IsNullOrWhiteSpace(candidate.DecisionOption))
        {
            var closesAt = existing.DecisionClosesAt ?? candidate.DecisionClosesAt;
            if (closesAt is null || candidate.ClaimedAt < closesAt)
            {
                existing.DecisionOption = candidate.DecisionOption;
                existing.DecidedAt = candidate.ClaimedAt;
                existing.DecisionClosesAt ??= candidate.DecisionClosesAt;
            }
        }

        return Task.FromResult(new AZOAResult<PersonOperationClaim> { Result = existing });
    }
    /// <summary>D39: every live (unreleased) claim in one uniqueness domain.</summary>
    public Task<AZOAResult<IReadOnlyList<PersonOperationClaim>>> ListLiveByOperationAsync(
        string uniquenessScope, string operationKey, CancellationToken ct = default)
        => Task.FromResult(new AZOAResult<IReadOnlyList<PersonOperationClaim>>
        {
            Result = _rows
                .Where(r => string.Equals(r.UniquenessScope, uniquenessScope, StringComparison.Ordinal)
                    && string.Equals(r.OperationKey, operationKey, StringComparison.Ordinal)
                    && r.ReleasedAt is null)
                .ToList(),
        });


    public Task<AZOAResult<PersonOperationClaim?>> GetAsync(
        string uniquenessScope, string operationKey, string personKey, CancellationToken ct = default)
        => Task.FromResult(new AZOAResult<PersonOperationClaim?>
        {
            Result = _rows.FirstOrDefault(r =>
                string.Equals(r.UniquenessScope, uniquenessScope, StringComparison.Ordinal)
                && string.Equals(r.OperationKey, operationKey, StringComparison.Ordinal)
                && string.Equals(r.PersonKey, personKey, StringComparison.Ordinal)),
        });

    public Task<AZOAResult<bool>> ReleaseAsync(
        string uniquenessScope,
        string operationKey,
        string personKey,
        string userId,
        DateTimeOffset releasedAt,
        CancellationToken ct = default)
    {
        var row = _rows.FirstOrDefault(r =>
            string.Equals(r.UniquenessScope, uniquenessScope, StringComparison.Ordinal)
            && string.Equals(r.OperationKey, operationKey, StringComparison.Ordinal)
            && string.Equals(r.PersonKey, personKey, StringComparison.Ordinal)
            && string.Equals(r.UserId, userId, StringComparison.Ordinal)
            && r.ReleasedAt is null);

        if (row is null)
            return Task.FromResult(new AZOAResult<bool> { Result = false });

        row.ReleasedAt = releasedAt;
        return Task.FromResult(new AZOAResult<bool> { Result = true });
    }
}

/// <summary>
/// <see cref="IPersonUniquenessGate"/> over the same ledger the tally reads, so the
/// vote path and the count path share one source of truth exactly as production does.
/// </summary>
internal sealed class InMemoryPersonUniquenessGate(
    InMemoryPersonOperationClaimLedger ledger,
    InMemoryKycIdentityStore identities,
    TimeProvider clock) : IPersonUniquenessGate
{
    public async Task<AZOAResult<KycEligibilityDecision>> TryClaimAsync(
        PersonUniquenessRequirement requirement, Guid userId, Guid? tenantId, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(requirement, userId, tenantId, ct);
        if (resolved is null)
            return Decision(KycEligibilityDecision.Ineligible(KycIneligibilityCause.NoVerifiedIdentity));

        var (uniquenessScope, operationKey, personKey) = resolved.Value;
        var claimed = await ledger.TryClaimAsync(
            new PersonOperationClaim
            {
                Id = PersonOperationClaim.RecordIdFor(uniquenessScope, operationKey, personKey),
                UniquenessScope = uniquenessScope,
                OperationKey = operationKey,
                PersonKey = personKey,
                UserId = userId.ToString("N"),
                TenantId = tenantId?.ToString("N"),
                ClaimedAt = clock.GetUtcNow(),
                // D39: the decision IS the vote. Dropping these leaves DecisionOption
                // permanently null, the tally skips every row, and every assertion here
                // passes against a ledger that records nothing.
                DecisionOption = requirement.Decision,
                DecisionClosesAt = requirement.DecisionClosesAt,
            },
            ct);

        // The holder is READ BACK; the refusal is never decided by a preceding read.
        var held = claimed.Result;
        return held is not null && string.Equals(held.UserId, userId.ToString("N"), StringComparison.Ordinal)
            ? Decision(KycEligibilityDecision.Eligible())
            : Decision(KycEligibilityDecision.Ineligible(KycIneligibilityCause.PersonAlreadyClaimedOperation));
    }

    public async Task<AZOAResult<KycEligibilityDecision>> EvaluateAsync(
        PersonUniquenessRequirement requirement, Guid userId, Guid? tenantId, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(requirement, userId, tenantId, ct);
        if (resolved is null)
            return Decision(KycEligibilityDecision.Ineligible(KycIneligibilityCause.NoVerifiedIdentity));

        var (uniquenessScope, operationKey, personKey) = resolved.Value;
        var held = (await ledger.GetAsync(uniquenessScope, operationKey, personKey, ct)).Result;
        var free = held is null || held.ReleasedAt is not null
            || string.Equals(held.UserId, userId.ToString("N"), StringComparison.Ordinal);

        return Decision(free
            ? KycEligibilityDecision.Eligible()
            : KycEligibilityDecision.Ineligible(KycIneligibilityCause.PersonAlreadyClaimedOperation));
    }

    public async Task<AZOAResult<bool>> ReleaseAsync(
        PersonUniquenessRequirement requirement, Guid userId, Guid? tenantId, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(requirement, userId, tenantId, ct);
        if (resolved is null)
            return new AZOAResult<bool> { Result = false };

        var (uniquenessScope, operationKey, personKey) = resolved.Value;
        return await ledger.ReleaseAsync(
            uniquenessScope, operationKey, personKey, userId.ToString("N"), clock.GetUtcNow(), ct);
    }

    /// <summary>The (scope, operation key, person key) triple, or null when this user has no binding.</summary>
    private async Task<(string UniquenessScope, string OperationKey, string PersonKey)?> ResolveAsync(
        PersonUniquenessRequirement requirement, Guid userId, Guid? tenantId, CancellationToken ct)
    {
        var operationKey = PersonUniquenessRequirement.Canonicalize(requirement.OperationKey);
        if (operationKey is null)
            return null;

        var tenantScope = KycPersonKeyDeriver.ScopeValue(tenantId);
        var global = requirement.Scope == PersonUniquenessScope.Global;

        // Identity is always looked up in the TENANT scope, never the global sentinel;
        // only the person KEY changes with the uniqueness domain.
        var identity = (await identities.GetByUserAsync(tenantScope, userId.ToString("N"), ct)).Result;
        if (identity is null)
            return null;

        var personKey = global ? identity.GlobalPersonKey : identity.PersonKey;
        return string.IsNullOrWhiteSpace(personKey)
            ? null
            : (global ? KycPersonKeyDeriver.GlobalScopeSentinel : tenantScope, operationKey, personKey);
    }

    /// <summary>Every refusal is a SUCCESS result carrying an ineligible decision (never IsError).</summary>
    private static AZOAResult<KycEligibilityDecision> Decision(KycEligibilityDecision decision) =>
        new() { Result = decision };
}

/// <summary>
/// One tenant's governance world: a real <see cref="HolonManager"/> over an in-memory
/// store, a shared claim ledger, and the three D20 handlers wired to both.
/// </summary>
/// <remarks>
/// Every construction of a handler funnels through this class, so the D39 rewrite —
/// which changes what the tally is allowed to depend on — has ONE place to touch in
/// this directory. See <c>tests/AZOA.WebAPI.Tests/Governance/AGENTS.md</c>.
/// </remarks>
internal sealed class GovernanceWorld
{
    public static readonly DateTimeOffset Start = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private int _nextPerson;

    public GovernanceWorld(Guid? tenant = null)
    {
        Tenant = tenant ?? Guid.Parse("7e57a11a-0000-4000-8000-000000000001");
        Clock = new SteppableClock(Start);
        Holons = new InMemoryHolonStore();
        // The REAL manager: owner-or-public CanRead, the publication allow-list, and
        // the blind metadata merge on UpdateAsync are all the production rules.
        Manager = new HolonManager(Holons);
        Identities = new InMemoryKycIdentityStore();
        Claims = new InMemoryPersonOperationClaimLedger();
        Gate = new InMemoryPersonUniquenessGate(Claims, Identities, Clock);
    }

    public Guid Tenant { get; }

    public SteppableClock Clock { get; }

    public InMemoryHolonStore Holons { get; }

    public HolonManager Manager { get; }

    public InMemoryKycIdentityStore Identities { get; }

    public InMemoryPersonOperationClaimLedger Claims { get; }

    public InMemoryPersonUniquenessGate Gate { get; }

    /// <summary>A distinct verified human holding exactly one user, inside one tenant.</summary>
    public Guid VerifiedUser(Guid? tenant = null)
    {
        var userId = Guid.NewGuid();
        Identities.Bind(
            KycPersonKeyDeriver.ScopeValue(tenant ?? Tenant), userId, $"person-{++_nextPerson:D4}");
        return userId;
    }

    /// <summary>Creates a proposal through the real node handler and returns its holon id.</summary>
    public async Task<Guid> CreateProposalAsync(
        Guid owner,
        string[] options,
        DateTimeOffset? closesAt = null,
        string uniquenessScope = "Tenant",
        Guid? tenant = null)
    {
        var handler = new GovernanceProposalCreateNodeHandler(Manager, Clock);
        var result = await handler.HandleAsync(Context(
            ActionNodeType.GovernanceProposalCreate,
            new GovernanceProposalCreateNodeConfig
            {
                Name = "Proposal",
                Description = "Decided by the members.",
                Options = options.ToList(),
                ClosesAt = closesAt,
                UniquenessScope = uniquenessScope,
            },
            owner,
            tenant ?? Tenant));

        if (result.IsError)
            throw new InvalidOperationException($"Proposal setup failed: {result.Message}");

        return Guid.Parse(Read(result).GetProperty("proposalId").GetString()!);
    }

    /// <summary>Casts a ballot through the vote node — the only sanctioned path.</summary>
    public Task<ActionNodeHandlerResult> VoteAsync(Guid voter, Guid proposalId, string option, Guid? tenant = null)
    {
        var handler = new GovernanceVoteNodeHandler(Manager, Gate, Clock);
        return handler.HandleAsync(Context(
            ActionNodeType.GovernanceVote,
            new GovernanceVoteNodeConfig { ProposalHolonId = proposalId, Option = option },
            voter,
            tenant ?? Tenant));
    }

    /// <summary>Runs the tally node.</summary>
    public Task<ActionNodeHandlerResult> TallyAsync(Guid caller, Guid proposalId, Guid? tenant = null)
    {
        var handler = new GovernanceTallyNodeHandler(Manager, Claims, Clock);
        return handler.HandleAsync(Context(
            ActionNodeType.GovernanceTally,
            new GovernanceTallyNodeConfig { ProposalHolonId = proposalId },
            caller,
            tenant ?? Tenant));
    }

    /// <summary>
    /// The attack surface: <c>POST /api/holon</c> with a hand-written GovernanceVote
    /// child. Goes through the real <see cref="HolonManager"/>, so this is exactly
    /// what any verified user can do with an ordinary API token — no node handler, no
    /// uniqueness claim, no server-set timestamp.
    /// </summary>
    /// <returns>
    /// The forged holon's id, or null when the platform REFUSED the write. Refusal is
    /// a legitimate outcome — Phase 3 may retire the holon-ballot representation
    /// outright — so this reports rather than throws; the assertions are on the tally.
    /// </returns>
    public async Task<Guid?> ForgeBallotHolonAsync(Guid forger, Guid proposalId, string option, DateTimeOffset castAt)
    {
        var created = await Manager.CreateAsync(
            new HolonCreateModel
            {
                Name = "Ballot",
                Description = string.Empty,
                ParentHolonId = proposalId,
                AssetType = ConsumerGovernanceHolonTypes.VoteAssetType,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ConsumerGovernanceHolonTypes.VoteMetadata.Option] = option,
                    [ConsumerGovernanceHolonTypes.VoteMetadata.CastAt] =
                        castAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                },
            },
            forger);

        return created.IsError || created.Result is null ? null : created.Result.Id;
    }

    /// <summary>
    /// <c>PUT /api/holon/{id}</c> against every ballot holon on a proposal that the
    /// caller owns. <see cref="HolonManager.UpdateAsync"/> blind-merges caller
    /// metadata, so this is the live route by which a voter rewrites a ballot they
    /// already cast.
    /// </summary>
    /// <returns>
    /// How many rewrites the platform ACCEPTED. Zero is a legitimate outcome — closing
    /// this route is one valid way to satisfy AC4 — so this reports rather than
    /// throws; the assertion is on the tally.
    /// </returns>
    public async Task<int> RewriteBallotOptionsAsync(Guid proposalId, Guid owner, string option)
    {
        var accepted = 0;
        foreach (var ballot in BallotHolons(proposalId).Where(h => h.UserId == owner))
        {
            var updated = await Manager.UpdateAsync(
                ballot.Id,
                new HolonUpdateModel
                {
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [ConsumerGovernanceHolonTypes.VoteMetadata.Option] = option,
                    },
                },
                owner);

            if (!updated.IsError)
                accepted++;
        }

        return accepted;
    }

    /// <summary>Every GovernanceVote child of a proposal, whatever wrote it.</summary>
    public IReadOnlyList<IHolon> BallotHolons(Guid proposalId) =>
        Holons.All
            .Where(h => h.ParentHolonId == proposalId
                && string.Equals(h.AssetType, ConsumerGovernanceHolonTypes.VoteAssetType, StringComparison.Ordinal))
            .ToList();

    /// <summary>Live (unreleased) claim rows on a proposal's uniqueness domain.</summary>
    public IReadOnlyList<PersonOperationClaim> LiveClaimsOn(Guid proposalId)
    {
        var operationKey = ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor(proposalId);
        return Claims.Rows
            .Where(r => string.Equals(r.OperationKey, operationKey, StringComparison.Ordinal) && r.ReleasedAt is null)
            .ToList();
    }

    /// <summary>
    /// Redistributes the ballots' client-supplied <c>castAt</c> strings in reverse, so
    /// the order those strings imply is inverted and NOTHING else about the proposal
    /// changes. A no-op below two ballots, which is itself an answer: with no second
    /// ballot there is no ordering left to attack.
    /// </summary>
    public void ReverseBallotCastAtOrder(Guid proposalId)
    {
        var ballots = BallotHolons(proposalId)
            .OrderBy(h => h.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.VoteMetadata.CastAt), StringComparer.Ordinal)
            .ToList();

        var stamps = ballots
            .Select(h => h.Metadata.GetValueOrDefault(ConsumerGovernanceHolonTypes.VoteMetadata.CastAt))
            .Reverse()
            .ToList();

        for (var i = 0; i < ballots.Count; i++)
            if (stamps[i] is { } stamp)
                ballots[i].Metadata[ConsumerGovernanceHolonTypes.VoteMetadata.CastAt] = stamp;
    }

    /// <summary>The published counts, as any consumer of the tally node reads them.</summary>
    public static TallyReading ReadTally(ActionNodeHandlerResult result)
    {
        if (result.IsError)
            throw new InvalidOperationException($"Tally failed: {result.Message}");

        var output = Read(result);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var option in output.GetProperty("counts").EnumerateObject())
            counts[option.Name] = option.Value.GetInt32();

        return new TallyReading(
            output.GetProperty("totalVotes").GetInt32(),
            counts,
            output.GetProperty("tallyHash").GetString() ?? string.Empty);
    }

    private static JsonElement Read(ActionNodeHandlerResult result) =>
        JsonDocument.Parse(result.Output!).RootElement.Clone();

    private static ActionNodeExecutionContext Context(
        ActionNodeType nodeType, object config, Guid actingUserId, Guid? tenantId)
    {
        var node = new ActionNode
        {
            Id = Guid.NewGuid(),
            Name = nodeType.ToString(),
            NodeType = nodeType,
            Config = JsonSerializer.Serialize(config),
        };
        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            UserId = actingUserId,
            Nodes = new List<ActionNode> { node },
            Edges = new List<ActionEdge>(),
        };
        return new ActionNodeExecutionContext(Guid.NewGuid(), node.Id, action, actingUserId, null, tenantId);
    }
}

/// <summary>What the tally publishes: a total, per-option counts, and the digest over both.</summary>
internal sealed record TallyReading(int TotalVotes, SortedDictionary<string, int> Counts, string TallyHash);
