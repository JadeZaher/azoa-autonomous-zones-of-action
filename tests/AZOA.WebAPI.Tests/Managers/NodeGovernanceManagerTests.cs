using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Tests.Managers;

public sealed class NodeGovernanceManagerTests
{
    [Fact]
    public async Task GetParametersAsync_WithNoPersistedRow_ReturnsConfiguredDefaults()
    {
        var manager = new NodeGovernanceManager(
            new FakeNodeGovernanceStore(),
            Options.Create(new NodeGovernanceOptions
            {
                AllowedChains = new[] { "Algorand" },
                AllowedAssetTypes = new[] { "Song" },
            }));

        var result = await manager.GetParametersAsync();

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Version.Should().Be(0);
        result.Result.AllowedChains.Should().Equal("Algorand");
        result.Result.AllowedAssetTypes.Should().Equal("Song");
    }

    [Fact]
    public async Task UpdateParametersAsync_NormalizesLists_IncrementsVersion_AndWritesAudit()
    {
        var actor = Guid.NewGuid();
        var store = new FakeNodeGovernanceStore(new NodeGovernanceParameters
        {
            Id = NodeGovernanceParameters.LocalId,
            AllowedChains = new[] { "Solana" },
            AllowedAssetTypes = new[] { "Badge" },
            Version = 3,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        });
        var manager = new NodeGovernanceManager(store);

        var result = await manager.UpdateParametersAsync(new NodeGovernanceParametersUpdateRequest
        {
            ExpectedVersion = 3,
            AllowedChains = new[] { " algorand ", "ALGORAND", "Solana" },
            AllowedAssetTypes = Array.Empty<string>(),
        }, actor);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Version.Should().Be(4);
        result.Result.AllowedChains.Should().Equal("algorand", "Solana");
        result.Result.AllowedAssetTypes.Should().BeEmpty("an explicit empty list means deny-all");

        store.SavedParameters.Should().NotBeNull();
        store.SavedParameters!.UpdatedByAvatarId.Should().Contain(actor.ToString("N"));
        store.Audits.Should().ContainSingle();
        store.Audits[0].PreviousVersion.Should().Be(3);
        store.Audits[0].NewVersion.Should().Be(4);
        store.Audits[0].PreviousAllowedChains.Should().Equal("Solana");
        store.Audits[0].AllowedChains.Should().Equal("algorand", "Solana");
        store.Audits[0].AllowedAssetTypes.Should().BeEmpty();
        store.LastExpectedVersion.Should().Be(3);
    }

    [Fact]
    public async Task UpdateParametersAsync_WithStaleExpectedVersion_RejectsBeforeStoreWrite()
    {
        var store = new FakeNodeGovernanceStore(new NodeGovernanceParameters
        {
            Id = NodeGovernanceParameters.LocalId,
            Version = 4,
        });
        var manager = new NodeGovernanceManager(store);

        var result = await manager.UpdateParametersAsync(new NodeGovernanceParametersUpdateRequest
        {
            ExpectedVersion = 3,
            AllowedChains = new[] { "Algorand" },
        }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("version conflict");
        store.SavedParameters.Should().BeNull();
        store.Audits.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateParametersAsync_WithBlankEntry_RejectsBeforeStoreWrite()
    {
        var store = new FakeNodeGovernanceStore();
        var manager = new NodeGovernanceManager(store);

        var result = await manager.UpdateParametersAsync(new NodeGovernanceParametersUpdateRequest
        {
            AllowedChains = new[] { "Algorand", " " },
        }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("AllowedChains cannot contain blank entries");
        store.SavedParameters.Should().BeNull();
        store.Audits.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateParametersAsync_WithStoreReadFailure_RejectsFailClosed()
    {
        var manager = new NodeGovernanceManager(new FakeNodeGovernanceStore(error: "surreal unavailable"));

        var result = await manager.UpdateParametersAsync(new NodeGovernanceParametersUpdateRequest
        {
            AllowedChains = new[] { "Algorand" },
        }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("parameters unavailable");
    }

    [Fact]
    public async Task ListAuditAsync_ClampsLimit()
    {
        var store = new FakeNodeGovernanceStore();
        var manager = new NodeGovernanceManager(store);

        var result = await manager.ListAuditAsync(10_000);

        result.IsError.Should().BeFalse(result.Message);
        store.LastAuditLimit.Should().Be(100);
    }

    private sealed class FakeNodeGovernanceStore : INodeGovernanceStore
    {
        private readonly NodeGovernanceParameters? _initialParameters;
        private readonly string? _error;

        public FakeNodeGovernanceStore(NodeGovernanceParameters? initialParameters = null, string? error = null)
        {
            _initialParameters = initialParameters;
            _error = error;
        }

        public NodeGovernanceParameters? SavedParameters { get; private set; }

        public List<NodeGovernanceAudit> Audits { get; } = new();

        public int? LastAuditLimit { get; private set; }

        public long? LastExpectedVersion { get; private set; }

        public Task<AZOAResult<NodeGovernanceParameters?>> GetParametersAsync(CancellationToken ct = default)
            => Task.FromResult(_error is not null
                ? new AZOAResult<NodeGovernanceParameters?> { IsError = true, Message = _error }
                : new AZOAResult<NodeGovernanceParameters?> { Result = SavedParameters ?? _initialParameters, Message = "Success" });

        public Task<AZOAResult<NodeGovernanceParameters>> UpdateParametersWithAuditAsync(
            NodeGovernanceParameters parameters,
            NodeGovernanceAudit audit,
            long? expectedVersion,
            CancellationToken ct = default)
        {
            LastExpectedVersion = expectedVersion;
            SavedParameters = parameters;
            Audits.Add(audit);
            return Task.FromResult(new AZOAResult<NodeGovernanceParameters> { Result = parameters, Message = "Saved." });
        }

        public Task<AZOAResult<IEnumerable<NodeGovernanceAudit>>> ListAuditAsync(int limit, CancellationToken ct = default)
        {
            LastAuditLimit = limit;
            return Task.FromResult(new AZOAResult<IEnumerable<NodeGovernanceAudit>>
            {
                Result = Audits,
                Message = "Success",
            });
        }
    }
}
