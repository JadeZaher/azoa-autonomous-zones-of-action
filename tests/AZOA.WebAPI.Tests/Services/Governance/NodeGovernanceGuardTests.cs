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

        (await guard.EnsureAllowedAsync("Algorand", "Song", "holon:create")).IsError.Should().BeFalse();
        (await guard.EnsureAllowedAsync(null, null, "internal")).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task ConfiguredAllowlists_AreCaseInsensitiveAndTrimmed()
    {
        var guard = Build(new NodeGovernanceOptions
        {
            AllowedChains = new[] { " algorand " },
            AllowedAssetTypes = new[] { " Song " }
        });

        (await guard.EnsureAllowedAsync("ALGORAND", "song", "allocation:Mint")).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task EmptyConfiguredAllowlist_DeniesEveryValue()
    {
        var guard = Build(new NodeGovernanceOptions { AllowedChains = Array.Empty<string>() });

        var result = await guard.EnsureChainAllowedAsync("Algorand", "allocation:Mint");

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Allowed chains: <none>");
    }

    [Fact]
    public async Task ConfiguredAssetAllowlist_RequiresAnAssetType()
    {
        var guard = Build(new NodeGovernanceOptions { AllowedAssetTypes = new[] { "Song" } });

        var result = await guard.EnsureAssetTypeAllowedAsync(" ", "holon:create");

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("requires an asset type");
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

        allowed.IsError.Should().BeFalse();
        denied.IsError.Should().BeTrue();
        denied.Message.Should().Contain("Allowed chains: Algorand");
    }

    [Fact]
    public async Task PersistedStoreError_DeniesFailClosed()
    {
        var guard = Build(new NodeGovernanceOptions(), new FakeNodeGovernanceStore(error: "store offline"));

        var result = await guard.EnsureAllowedAsync("Algorand", "Song", "allocation:Mint");

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("parameters unavailable");
        result.Message.Should().Contain("store offline");
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
