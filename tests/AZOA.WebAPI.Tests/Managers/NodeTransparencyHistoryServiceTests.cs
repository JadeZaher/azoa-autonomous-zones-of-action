using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Conformance;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Moq;

namespace AZOA.WebAPI.Tests.Managers;

public sealed class NodeTransparencyHistoryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "azoa-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TryGetAsync_SignsOrderedRedactedHistoryAndExtendsOnlyFromPriorHead()
    {
        var actor = Guid.NewGuid();
        var governance = new List<NodeGovernanceAudit> { Governance(1, actor, "private-governance-id") };
        var fees = new List<NodeFeeAudit> { Fee(1, actor, "private-fee-id") };
        var treasury = new List<NodeTreasuryAudit>();
        var service = CreateService(governance, fees, treasury);

        var first = await service.TryGetAsync();
        treasury.Add(Treasury(1, actor, "private-treasury-id"));
        var second = await service.TryGetAsync();

        first.IsAvailable.Should().BeTrue();
        second.IsAvailable.Should().BeTrue();
        first.Document!.Checkpoint.AuditEventCount.Should().Be(2);
        second.Document!.Checkpoint.AuditEventCount.Should().Be(3);
        NodeTransparencyHistoryVerifier.TryVerify(second.Document, out var failure).Should().BeTrue();
        failure.Should().Be(NodeTransparencyHistoryVerificationFailure.UnsupportedVersion);
        second.Document.Entries.Should().Equal(second.Document.Entries
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Kind, StringComparer.Ordinal)
            .ThenBy(entry => entry.EntrySha256, StringComparer.Ordinal));

        var json = JsonSerializer.Serialize(second.Document, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().NotContain(actor.ToString("N"));
        json.Should().NotContain("private-governance-id");
        json.Should().NotContain("private-fee-id");
        json.Should().NotContain("private-treasury-id");
        json.Should().NotContain("operator-only-detail");
        json.Should().NotContain("updatedBy");
    }

    [Fact]
    public async Task TryGetAsync_RejectsHistoryRewriteAfterProtectedCheckpoint()
    {
        var actor = Guid.NewGuid();
        var governance = new List<NodeGovernanceAudit> { Governance(1, actor, "internal-id") };
        var fees = new List<NodeFeeAudit>();
        var treasury = new List<NodeTreasuryAudit>();
        var service = CreateService(governance, fees, treasury);

        var first = await service.TryGetAsync();
        governance[0].AllowedChains = ["Tampered"];
        var rewritten = await service.TryGetAsync();

        first.IsAvailable.Should().BeTrue();
        rewritten.Should().Be(NodeTransparencyHistoryAvailability.Unavailable);
    }

    [Fact]
    public async Task Verifier_RejectsPayloadAndOrderingTampering()
    {
        var actor = Guid.NewGuid();
        var service = CreateService(
            [Governance(1, actor, "one")],
            [Fee(1, actor, "two")],
            []);
        var available = await service.TryGetAsync();
        var document = available.Document!;

        var payloadTampered = document with
        {
            Entries = document.Entries.Select((entry, index) => index == 0
                ? entry with { PayloadJson = "{}" }
                : entry).ToArray(),
        };
        var orderTampered = document with { Entries = document.Entries.Reverse().ToArray() };

        NodeTransparencyHistoryVerifier.TryVerify(payloadTampered, out var payloadFailure).Should().BeFalse();
        payloadFailure.Should().Be(NodeTransparencyHistoryVerificationFailure.InvalidEntry);
        NodeTransparencyHistoryVerifier.TryVerify(orderTampered, out var orderFailure).Should().BeFalse();
        orderFailure.Should().Be(NodeTransparencyHistoryVerificationFailure.InvalidOrdering);
    }

    private NodeTransparencyHistoryService CreateService(
        List<NodeGovernanceAudit> governance,
        List<NodeFeeAudit> fees,
        List<NodeTreasuryAudit> treasury)
    {
        var store = new Mock<INodeTransparencyStore>(MockBehavior.Strict);
        store.Setup(value => value.ListGovernanceAuditAsync(It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Success<IReadOnlyList<NodeGovernanceAudit>>(governance.ToArray()));
        store.Setup(value => value.ListFeeAuditAsync(It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Success<IReadOnlyList<NodeFeeAudit>>(fees.ToArray()));
        store.Setup(value => value.ListTreasuryAuditAsync(It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Success<IReadOnlyList<NodeTreasuryAudit>>(treasury.ToArray()));
        var identityOptions = Options.Create(new NodeConformanceOptions
        {
            Enabled = true,
            NodeId = "node-alpha",
            KeyStoragePath = _directory,
        });
        var provider = new EphemeralDataProtectionProvider();
        var keys = new ProtectedFileNodeIdentityKeyService(provider, identityOptions);
        return new NodeTransparencyHistoryService(
            store.Object,
            keys,
            new NodeTransparencyHistoryCheckpointStore(provider, identityOptions),
            Options.Create(new NodeTransparencyHistoryOptions { Enabled = true, MaxAuditEntries = 32 }),
            identityOptions,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-13T01:00:00Z")));
    }

    private static NodeGovernanceAudit Governance(long version, Guid actor, string id)
        => new()
        {
            Id = id,
            Action = "ParametersUpdated",
            ActorAvatarId = $"avatar:{actor:N}",
            PreviousVersion = version - 1,
            NewVersion = version,
            AllowedChains = ["Algorand"],
            AllowedAssetTypes = ["NFT"],
            Detail = "operator-only-detail",
            OccurredAt = DateTimeOffset.Parse("2026-07-13T00:01:00Z"),
        };

    private static NodeFeeAudit Fee(long version, Guid actor, string id)
    {
        var schedule = new NodeFeeScheduleResponse
        {
            Mint = new NodeFeeScheduleEntryResponse { FlatBaseUnits = "1", Bps = 0 },
            Transfer = new NodeFeeScheduleEntryResponse(),
            Swap = new NodeFeeScheduleEntryResponse(),
            QuestComplete = new NodeFeeScheduleEntryResponse(),
            FederationPublish = new NodeFeeScheduleEntryResponse(),
            Version = version,
            UpdatedByAvatarId = actor,
        };
        return new NodeFeeAudit
        {
            Id = id,
            Action = "ScheduleUpdated",
            ActorAvatarId = $"avatar:{actor:N}",
            PreviousVersion = version - 1,
            NewVersion = version,
            ScheduleJson = JsonSerializer.Serialize(schedule, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Detail = "operator-only-detail",
            OccurredAt = DateTimeOffset.Parse("2026-07-13T00:02:00Z"),
        };
    }

    private static NodeTreasuryAudit Treasury(long version, Guid actor, string id)
    {
        var destination = new NodeTreasuryDestinationResponse
        {
            Chain = "Simulated",
            Network = ChainNetwork.Devnet,
            Address = "sim:public-address",
            Version = version,
            UpdatedByAvatarId = actor,
        };
        return new NodeTreasuryAudit
        {
            Id = id,
            Action = "DestinationUpdated",
            ActorAvatarId = $"avatar:{actor:N}",
            Chain = "Simulated",
            Network = nameof(ChainNetwork.Devnet),
            PreviousVersion = version - 1,
            NewVersion = version,
            DestinationJson = JsonSerializer.Serialize(destination, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Detail = "operator-only-detail",
            OccurredAt = DateTimeOffset.Parse("2026-07-13T00:03:00Z"),
        };
    }

    private static AZOAResult<T> Success<T>(T value) => new() { Result = value, Message = "Success" };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
