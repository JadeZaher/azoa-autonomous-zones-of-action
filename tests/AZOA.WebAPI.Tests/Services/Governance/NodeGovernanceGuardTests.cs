using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Tests.Services.Governance;

public class NodeGovernanceGuardTests
{
    [Fact]
    public async Task UnconfiguredAllowlists_AreUnrestricted()
    {
        var guard = Build(new NodeGovernanceOptions());

        (await guard.EnsureAllowedAsync("Algorand", "Song", "holon:create")).IsAllowed.Should().BeTrue();
        (await guard.EnsureAllowedAsync(null, null, "internal")).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task ConfiguredAllowlists_AreCaseInsensitiveAndTrimmed()
    {
        var guard = Build(new NodeGovernanceOptions
        {
            AllowedChains = new[] { " algorand " },
            AllowedAssetTypes = new[] { " Song " }
        });

        (await guard.EnsureAllowedAsync("ALGORAND", "song", "allocation:Mint")).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task EmptyConfiguredAllowlist_DeniesEveryValue()
    {
        var guard = Build(new NodeGovernanceOptions { AllowedChains = Array.Empty<string>() });

        var result = await guard.EnsureChainAllowedAsync("Algorand", "allocation:Mint");

        result.IsRefused.Should().BeTrue();
        // The allowlist itself is operator detail: it names rails. What comes back is the
        // policy refusal, and the wording that names "<none>" goes to the guard's log.
        result.ConsumerMessage.Should().NotContain("chain");
        ConsumerSafeErrors.LeaksRailDetail(result.ConsumerMessage).Should().BeFalse();
        result.Code.Should().Be(ConsumerSafeErrorCodes.NotPermitted);
    }

    [Fact]
    public async Task ConfiguredAssetAllowlist_RequiresAnAssetType()
    {
        var guard = Build(new NodeGovernanceOptions { AllowedAssetTypes = new[] { "Song" } });

        var result = await guard.EnsureAssetTypeAllowedAsync(" ", "holon:create");

        result.IsRefused.Should().BeTrue();
        // "asset type" is not rail vocabulary, so this wording survives the scrub intact -
        // proof the counter redacts what leaks and passes through what does not.
        result.ConsumerMessage.Should().Contain("requires an asset type");
        result.Code.Should().Be(ConsumerSafeErrorCodes.NotPermitted);
    }

    [Fact]
    public async Task PersistedParameters_OverrideConfiguredOptions()
    {
        var store = new FakeNodeGovernanceStore(new NodeGovernanceParameters
        {
            Id = NodeGovernanceParameters.LocalId,
            AllowedChains = new[] { "Algorand" },
            AllowedAssetTypes = new[] { "Song" },
            Version = 1,
        });
        var guard = Build(
            new NodeGovernanceOptions { AllowedChains = new[] { "Solana" }, AllowedAssetTypes = new[] { "Badge" } },
            store);

        var allowed = await guard.EnsureAllowedAsync("Algorand", "Song", "allocation:Mint");
        var denied = await guard.EnsureAllowedAsync("Solana", "Badge", "allocation:Mint");

        allowed.IsAllowed.Should().BeTrue();
        denied.IsRefused.Should().BeTrue();
        denied.ConsumerMessage.Should().NotContain("Algorand");
        ConsumerSafeErrors.LeaksRailDetail(denied.ConsumerMessage).Should().BeFalse();
    }

    [Fact]
    public async Task PersistedStoreError_DeniesFailClosed()
    {
        var guard = Build(new NodeGovernanceOptions(), new FakeNodeGovernanceStore(error: "store offline"));

        var result = await guard.EnsureAllowedAsync("Algorand", "Song", "allocation:Mint");

        result.IsRefused.Should().BeTrue();
        result.Code.Should().Be(
            ConsumerSafeErrorCodes.RailUnavailable,
            "an unreadable policy store is a dependency failure, not a policy one");
        ConsumerSafeErrors.LeaksRailDetail(result.ConsumerMessage).Should().BeFalse();
        result.ConsumerMessage.Should().NotContain(
            "store offline", "the store's own words are operator detail, not a member's sentence");
    }

    [Theory]
    // The pass-through branch: no rail vocabulary anywhere, so the guard's own sentence survives
    // and reaches the caller verbatim. The type's remarks used to claim it never could.
    [InlineData("Node governance disallows holon:create on asset type Badge. Allowed asset types: Song.")]
    // The replacement branch: the same shape of sentence, naming a rail.
    [InlineData("Node governance disallows allocation:Mint on chain 'Algorand'. Allowed chains: Solana.")]
    [InlineData("The asset id 12345678 is not opted in; min balance is short by 100000 microalgo.")]
    public void RefusePolicy_NeverLetsRailDetailThroughEitherBranch(string operatorDetail)
    {
        // The invariant is a property of the OUTPUT, not of the input: whether the wording is
        // kept or replaced, what a member can read carries no rail detail. Asserting it on the
        // constructed value is the difference between pinning the rule and restating it.
        var decision = NodeGovernanceDecision.RefusePolicy(operatorDetail);

        decision.IsRefused.Should().BeTrue();
        decision.Code.Should().Be(ConsumerSafeErrorCodes.NotPermitted);
        ConsumerSafeErrors.LeaksRailDetail(decision.ConsumerMessage).Should().BeFalse(
            "the sentence a member reads is the one thing this type may hand out");
    }

    [Fact]
    public void RefusePolicy_KeepsRailFreeWordingAndReplacesTheRest()
    {
        // Both halves in one place, so the class-level remarks can be checked against behaviour
        // rather than against a second paragraph of prose.
        const string railFree =
            "Node governance requires an asset type on holon:create. Allowed asset types: Song.";
        const string leaking =
            "Node governance disallows allocation:Mint on chain 'Algorand'. Allowed chains: Solana.";

        NodeGovernanceDecision.RefusePolicy(railFree).ConsumerMessage.Should().Be(railFree);

        var replaced = NodeGovernanceDecision.RefusePolicy(leaking);
        replaced.ConsumerMessage.Should().NotContain("Algorand").And.NotContain("Solana");
        replaced.ConsumerMessage.Should().NotBe(leaking);
    }

    private static NodeGovernanceGuard Build(NodeGovernanceOptions options)
        => new(Options.Create(options));

    private static NodeGovernanceGuard Build(NodeGovernanceOptions options, INodeGovernanceStore store)
        => new(Options.Create(options), store);

    private sealed class FakeNodeGovernanceStore : INodeGovernanceStore
    {
        private readonly NodeGovernanceParameters? _parameters;
        private readonly string? _error;

        public FakeNodeGovernanceStore(NodeGovernanceParameters? parameters = null, string? error = null)
        {
            _parameters = parameters;
            _error = error;
        }

        public Task<AZOAResult<NodeGovernanceParameters?>> GetParametersAsync(CancellationToken ct = default)
            => Task.FromResult(_error is not null
                ? new AZOAResult<NodeGovernanceParameters?> { IsError = true, Message = _error }
                : new AZOAResult<NodeGovernanceParameters?> { Result = _parameters, Message = "Success" });

        public Task<AZOAResult<NodeGovernanceParameters>> UpdateParametersWithAuditAsync(
            NodeGovernanceParameters parameters,
            NodeGovernanceAudit audit,
            long? expectedVersion,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<IEnumerable<NodeGovernanceAudit>>> ListAuditAsync(int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
