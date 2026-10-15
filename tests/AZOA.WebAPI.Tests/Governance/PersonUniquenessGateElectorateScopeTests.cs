// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Tests.Governance;

/// <summary>
/// W9.2 — the KYC-binding tenant-scope resolution inside the REAL
/// <see cref="PersonUniquenessGate"/>, pinned BEFORE any KYC adapter is wired to it.
/// </summary>
/// <remarks>
/// <para>
/// The gate answers a question in three coordinates: which scope the IDENTITY is looked
/// up in, which scope the CLAIM is written in, and which person key is used. They are not
/// the same coordinate, and governance is where a mismatch becomes a wrong count: the
/// tally derives its domain from the proposal's recorded electorate, so the vote and the
/// count must land in the identical scope value by construction. These tests assert that
/// equality directly rather than trusting two code paths to agree.
/// </para>
/// <para>
/// They also pin the collision that made a tenant-less electorate node-wide:
/// <see cref="KycPersonKeyDeriver.ScopeValue"/> of a null tenant IS
/// <see cref="KycPersonKeyDeriver.GlobalScopeSentinel"/>, so a caller acting under no
/// tenant claims a "per-tenant" domain that is in fact the node's. Being explicit about it
/// here is what makes the electorate refusal in <c>HolonManager</c> legible rather than
/// arbitrary. See <c>Services/Governance/AGENTS.md</c> §electorate-must-name-a-tenant.
/// </para>
/// <para>
/// The general gate contract (one winner, the read-back, the sentinel round trip, one
/// refusal sentence) is pinned in <c>tests/AZOA.WebAPI.Tests/Kyc/PersonUniquenessGateTests.cs</c>
/// and deliberately not restated here.
/// </para>
/// </remarks>
public class PersonUniquenessGateElectorateScopeTests
{
    private const string OperationKey = "governance:proposal:0f1e2d3c";

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    // ── which scope the IDENTITY is resolved in ─────────────────────────────

    /// <summary>
    /// A node-global claim still resolves the acting user through the ACTING TENANT's
    /// binding scope. Only the person KEY changes with the uniqueness domain.
    /// </summary>
    [Fact]
    public async Task GlobalClaim_ResolvesTheIdentityInTheActingTenantsScope_ThenUsesTheGlobalPersonKey()
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var world = new GateWorld();
        world.Identities.Bind(
            KycPersonKeyDeriver.ScopeValue(tenant), user, "person-in-tenant", globalPersonKey: "one-human");

        var decision = await world.Gate.TryClaimAsync(
            Requirement(PersonUniquenessScope.Global), user, tenant);

        decision.Result!.IsEligible.Should().BeTrue();
        var claim = world.Claims.Rows.Single();
        claim.PersonKey.Should().Be("one-human", "a node-global claim spends the node-global key");
        claim.UniquenessScope.Should().Be(KycPersonKeyDeriver.GlobalScopeSentinel);
    }

    /// <summary>
    /// The negative half, and the one that would go quietly wrong: an identity that exists
    /// only in the GLOBAL sentinel scope is not a binding for a caller acting under a
    /// tenant. If the gate ever looked the identity up in the uniqueness scope instead of
    /// the tenant scope, this human would resolve and the tenant salt would be bypassed.
    /// </summary>
    [Fact]
    public async Task GlobalClaim_DoesNotResolveAnIdentityBoundOnlyInTheGlobalScope()
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var world = new GateWorld();
        world.Identities.Bind(
            KycPersonKeyDeriver.GlobalScopeSentinel, user, "person-node-wide", globalPersonKey: "one-human");

        var decision = await world.Gate.TryClaimAsync(
            Requirement(PersonUniquenessScope.Global), user, tenant);

        decision.IsError.Should().BeFalse("a missing binding is a refusal, never an error");
        decision.Result!.IsEligible.Should().BeFalse();
        decision.Result.Cause.Should().Be(KycIneligibilityCause.NoVerifiedIdentity);
    }

    // ── the vote and the count must share one domain ────────────────────────

    /// <summary>
    /// The scope the gate CLAIMS in must equal the scope the tally COUNTS in, and the
    /// tally derives its scope from the proposal's recorded electorate. Asserting the two
    /// values equal is the whole guarantee: a tally in another domain publishes zero under
    /// a hash that verifies.
    /// </summary>
    [Theory]
    [InlineData(PersonUniquenessScope.Tenant)]
    [InlineData(PersonUniquenessScope.Global)]
    public async Task ClaimLandsInExactlyTheDomainTheElectorateWillBeCountedIn(PersonUniquenessScope scope)
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var world = new GateWorld();
        world.Identities.Bind(KycPersonKeyDeriver.ScopeValue(tenant), user, "person-a");

        var proposalMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConsumerGovernanceHolonTypes.ProposalMetadata.ElectorateTenant] =
                ConsumerGovernanceHolonTypes.ElectorateValueFor(tenant),
        };

        var claimed = await world.Gate.TryClaimAsync(Requirement(scope), user, tenant);

        claimed.Result!.IsEligible.Should().BeTrue();
        ConsumerGovernanceHolonTypes
            .TryResolveUniquenessScopeValue(proposalMetadata, scope, out var countedIn)
            .Should().BeTrue();
        world.Claims.Rows.Single().UniquenessScope.Should().Be(countedIn);
    }

    // ── why a tenant-less electorate is the whole node ──────────────────────

    /// <summary>
    /// A caller acting under NO tenant claims a "per-tenant" domain that is in fact the
    /// node-global one, because the canonical scope rule maps a null tenant onto the
    /// global sentinel. That is not a bug in the gate — it is the reason a proposal may
    /// not record the node electorate.
    /// </summary>
    [Fact]
    public async Task ATenantLessCallersTenantScope_IsTheNodeGlobalScope()
    {
        var user = Guid.NewGuid();
        var world = new GateWorld();
        world.Identities.Bind(KycPersonKeyDeriver.ScopeValue(null), user, "person-with-no-tenant");

        var claimed = await world.Gate.TryClaimAsync(Requirement(PersonUniquenessScope.Tenant), user, tenantId: null);

        claimed.Result!.IsEligible.Should().BeTrue();
        world.Claims.Rows.Single().UniquenessScope
            .Should().Be(
                KycPersonKeyDeriver.GlobalScopeSentinel,
                "ScopeValue(null) IS the global sentinel, so 'this tenant' and 'the node' are one domain");
    }

    /// <summary>
    /// The same fact stated where a proposal states it: an electorate of <c>node</c>
    /// resolves to the node-global domain, and the holon chokepoint refuses to record it.
    /// </summary>
    [Fact]
    public void TheNodeElectorateResolvesToTheNodeGlobalDomain_AndIsRefusedBecauseOfIt()
    {
        var nodeElectorate = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConsumerGovernanceHolonTypes.ProposalMetadata.ElectorateTenant] =
                ConsumerGovernanceHolonTypes.NodeElectorate,
        };

        ConsumerGovernanceHolonTypes
            .TryResolveUniquenessScopeValue(nodeElectorate, PersonUniquenessScope.Tenant, out var scopeValue)
            .Should().BeTrue();
        scopeValue.Should().Be(KycPersonKeyDeriver.GlobalScopeSentinel);

        ConsumerGovernanceHolonTypes.IsUsableElectorateValue(ConsumerGovernanceHolonTypes.NodeElectorate)
            .Should().BeFalse();
        ConsumerGovernanceHolonTypes.ProposalElectorateRejection(
                ConsumerGovernanceHolonTypes.ProposalAssetType, nodeElectorate)
            .Should().NotBeNull();
    }

    /// <summary>
    /// A proposal that records NO electorate is inert rather than universal: nothing
    /// resolves, so nobody is admitted and nothing can be counted. It is therefore left
    /// writable — only a recorded-but-unusable electorate is a franchise nobody granted.
    /// </summary>
    [Fact]
    public void AProposalThatRecordsNoElectorate_IsInert_NotUniversal()
    {
        var none = new Dictionary<string, string>(StringComparer.Ordinal);

        ConsumerGovernanceHolonTypes.IsInElectorate(none, Guid.NewGuid()).Should().BeFalse();
        ConsumerGovernanceHolonTypes.IsInElectorate(none, null).Should().BeFalse();
        ConsumerGovernanceHolonTypes
            .TryResolveUniquenessScopeValue(none, PersonUniquenessScope.Tenant, out _)
            .Should().BeFalse();
        ConsumerGovernanceHolonTypes
            .ProposalElectorateRejection(ConsumerGovernanceHolonTypes.ProposalAssetType, none)
            .Should().BeNull();
    }

    // ── an unresolved subject never lands a vote ────────────────────────────

    /// <summary>
    /// A user bound in ANOTHER tenant is unresolved here. The claim round trip still runs
    /// — that is what keeps "no identity" and "already claimed" the same cost — but the
    /// sentinel row it writes must carry NO decision, or a human with no verified identity
    /// in this electorate has a live vote in the ledger.
    /// </summary>
    [Fact]
    public async Task AUserBoundInAnotherTenant_IsUnresolved_AndTheSentinelRowRecordsNoDecision()
    {
        var home = Guid.NewGuid();
        var elsewhere = Guid.NewGuid();
        var user = Guid.NewGuid();
        var world = new GateWorld();
        world.Identities.Bind(KycPersonKeyDeriver.ScopeValue(home), user, "person-at-home");

        var decision = await world.Gate.TryClaimAsync(
            Requirement(PersonUniquenessScope.Tenant, "yes"), user, elsewhere);

        decision.Result!.IsEligible.Should().BeFalse();
        decision.Result.Cause.Should().Be(KycIneligibilityCause.NoVerifiedIdentity);

        var row = world.Claims.Rows.Single();
        row.PersonKey.Should().StartWith(PersonUniquenessGate.SentinelPersonKeyPrefix);
        row.DecisionOption.Should().BeNull("a sentinel row holding a decision is a vote by nobody");
    }

    /// <summary>An erased binding is treated the same way, with its own operator cause.</summary>
    [Fact]
    public async Task AnErasedIdentity_IsUnresolved_AndRecordsNoDecision()
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var world = new GateWorld();
        world.Identities.Bind(
            KycPersonKeyDeriver.ScopeValue(tenant), user, "person-a", erasedAt: Now.AddDays(-1));

        var decision = await world.Gate.TryClaimAsync(
            Requirement(PersonUniquenessScope.Tenant, "yes"), user, tenant);

        decision.Result!.IsEligible.Should().BeFalse();
        decision.Result.Cause.Should().Be(KycIneligibilityCause.IdentityErased);
        world.Claims.Rows.Single().DecisionOption.Should().BeNull();
    }

    /// <summary>
    /// With no root secret configured the node cannot derive a person key at all, so the
    /// refusal is subject-INDEPENDENT and owes no round trip. Nothing reaches the ledger,
    /// which is what stops an unkeyed deployment from writing claims it can never
    /// re-derive.
    /// </summary>
    [Fact]
    public async Task WithNoConfiguredRootSecret_NothingReachesTheLedger()
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var world = new GateWorld(personKeySecret: null);
        world.Identities.Bind(KycPersonKeyDeriver.ScopeValue(tenant), user, "person-a");

        var decision = await world.Gate.TryClaimAsync(
            Requirement(PersonUniquenessScope.Tenant, "yes"), user, tenant);

        decision.Result!.IsEligible.Should().BeFalse();
        decision.Result.Cause.Should().Be(KycIneligibilityCause.UniquenessUnavailable);
        world.Claims.Rows.Should().BeEmpty();
    }

    private static PersonUniquenessRequirement Requirement(
        PersonUniquenessScope scope, string? decision = null) =>
        new()
        {
            OperationKey = OperationKey,
            Scope = scope,
            Decision = decision,
            DecisionClosesAt = decision is null ? null : Now.AddDays(1),
        };

    /// <summary>
    /// The REAL gate over the same in-memory identity store and claim ledger the rest of
    /// this directory uses, plus the REAL key deriver — so the canonical scope rule under
    /// test is the production one and not a copy of it.
    /// </summary>
    private sealed class GateWorld
    {
        public GateWorld(string? personKeySecret = "0123456789abcdef0123456789abcdef0123456789")
        {
            Identities = new InMemoryKycIdentityStore();
            Claims = new InMemoryPersonOperationClaimLedger();
            var deriver = new KycPersonKeyDeriver(Options.Create(new KycSettings
            {
                Identity = new KycIdentitySettings { PersonKeySecret = personKeySecret },
            }));
            Gate = new PersonUniquenessGate(
                Identities, Claims, deriver, logger: null, clock: new SteppableClock(Now));
        }

        public InMemoryKycIdentityStore Identities { get; }

        public InMemoryPersonOperationClaimLedger Claims { get; }

        public PersonUniquenessGate Gate { get; }
    }
}
