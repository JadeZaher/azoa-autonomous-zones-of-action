using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Moq;

namespace AZOA.WebAPI.Tests.Managers;

public sealed class NodeTransparencyManagerTests
{
    [Fact]
    public async Task GetSnapshotAsync_ProjectsOnlyPublicFieldsAndStableContentHash()
    {
        var actorId = Guid.NewGuid();
        var governance = new Mock<INodeGovernanceManager>();
        governance.Setup(manager => manager.GetParametersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new NodeGovernanceParametersResponse
            {
                AllowedChains = ["Algorand"],
                AllowedAssetTypes = ["NFT"],
                Version = 4,
                UpdatedByAvatarId = actorId,
                UpdatedAt = DateTimeOffset.Parse("2026-07-11T12:00:00Z"),
            }));
        var fees = new Mock<INodeFeeScheduleManager>();
        fees.Setup(manager => manager.GetScheduleAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(FeeSchedule(7, actorId)));
        var store = new Mock<INodeTransparencyStore>();
        store.Setup(value => value.ListTreasuryDestinationsAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success<IReadOnlyList<NodeTreasuryDestination>>(
            [
                new NodeTreasuryDestination
                {
                    Id = "private-record-id",
                    Chain = "Simulated",
                    Network = ChainNetwork.Devnet.ToString(),
                    Address = "sim:public-treasury",
                    Version = 2,
                    UpdatedByAvatarId = $"avatar:{actorId:N}",
                    UpdatedAt = DateTimeOffset.Parse("2026-07-11T12:02:00Z"),
                },
            ]));
        var manager = Build(governance.Object, fees.Object, store.Object);

        var first = await manager.GetSnapshotAsync();
        var second = await manager.GetSnapshotAsync();

        first.IsError.Should().BeFalse();
        first.Result!.ContentSha256.Should().Be(second.Result!.ContentSha256);
        first.Result.CryptographicHistoryProofAvailable.Should().BeFalse();
        first.Result.TreasuryDestinations.Should().ContainSingle()
            .Which.Address.Should().Be("sim:public-treasury");
        var json = JsonSerializer.Serialize(first.Result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Contains(actorId.ToString(), StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        json.Contains(actorId.ToString("N"), StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        json.Contains("updatedBy", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        json.Contains("private-record-id", StringComparison.Ordinal).Should().BeFalse();
        json.Contains("scheduleJson", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public async Task ListFeeAuditAsync_UsesOpaqueStableCursorAndParsesTypedSnapshots()
    {
        var actorId = Guid.NewGuid();
        var governance = new Mock<INodeGovernanceManager>();
        var fees = new Mock<INodeFeeScheduleManager>();
        var store = new Mock<INodeTransparencyStore>();
        var rows = new[]
        {
            FeeAudit("node_fee_audit:internal-a", 3, "2026-07-11T12:03:00Z", actorId),
            FeeAudit("node_fee_audit:internal-b", 2, "2026-07-11T12:02:00Z", actorId),
            FeeAudit("node_fee_audit:internal-c", 1, "2026-07-11T12:01:00Z", actorId),
        };
        NodeTransparencyStoreCursor? observedCursor = null;
        store.Setup(value => value.ListFeeAuditAsync(
                It.IsAny<int>(),
                It.IsAny<NodeTransparencyStoreCursor?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, NodeTransparencyStoreCursor? before, CancellationToken _) =>
            {
                if (before is null)
                    return Success<IReadOnlyList<NodeFeeAudit>>(rows);

                observedCursor = before;
                return Success<IReadOnlyList<NodeFeeAudit>>([rows[2]]);
            });
        var manager = Build(governance.Object, fees.Object, store.Object);

        var first = await manager.ListFeeAuditAsync(limit: 2);
        var second = await manager.ListFeeAuditAsync(limit: 2, cursor: first.Result!.NextCursor);

        first.IsError.Should().BeFalse();
        first.Result.Items.Should().HaveCount(2);
        first.Result.Items[0].Schedule.Version.Should().Be(3);
        first.Result.NextCursor.Should().NotBeNullOrWhiteSpace();
        first.Result.NextCursor.Should().NotContain("internal-b");
        observedCursor.Should().Be(new NodeTransparencyStoreCursor(rows[1].OccurredAt, rows[1].Id));
        second.Result!.Items.Should().ContainSingle().Which.Schedule.Version.Should().Be(1);

        var json = JsonSerializer.Serialize(first.Result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Contains(actorId.ToString("N"), StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        json.Should().NotContain("internal-a");
        json.Should().NotContain("internal-b");
        json.Contains("scheduleJson", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        json.Contains("detail", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public async Task ListGovernanceAuditAsync_InvalidCursor_ReturnsSafeValidationErrorWithoutReadingStore()
    {
        var governance = new Mock<INodeGovernanceManager>();
        var fees = new Mock<INodeFeeScheduleManager>();
        var store = new Mock<INodeTransparencyStore>();
        var manager = Build(governance.Object, fees.Object, store.Object);

        var result = await manager.ListGovernanceAuditAsync(cursor: "not-a-protected-cursor");
        var oversized = await manager.ListGovernanceAuditAsync(cursor: new string('x', 2049));

        result.IsError.Should().BeTrue();
        result.Message.Should().Be(NodeTransparencyMessages.InvalidCursor);
        oversized.IsError.Should().BeTrue();
        oversized.Message.Should().Be(NodeTransparencyMessages.InvalidCursor);
        store.Verify(value => value.ListGovernanceAuditAsync(
            It.IsAny<int>(),
            It.IsAny<NodeTransparencyStoreCursor?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetSnapshotAsync_StoreErrorDoesNotExposeInternalMessage()
    {
        var governance = new Mock<INodeGovernanceManager>();
        governance.Setup(manager => manager.GetParametersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeGovernanceParametersResponse>
            {
                IsError = true,
                Message = "connection password=private failed",
            });
        var fees = new Mock<INodeFeeScheduleManager>();
        fees.Setup(manager => manager.GetScheduleAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(FeeSchedule(0, Guid.NewGuid())));
        var store = new Mock<INodeTransparencyStore>();
        store.Setup(value => value.ListTreasuryDestinationsAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success<IReadOnlyList<NodeTreasuryDestination>>([]));
        var manager = Build(governance.Object, fees.Object, store.Object);

        var result = await manager.GetSnapshotAsync();

        result.IsError.Should().BeTrue();
        result.Message.Should().Be(NodeTransparencyMessages.Unavailable);
        result.Message.Should().NotContain("private");
    }

    [Fact]
    public async Task GovernanceAndTreasuryAudit_ExcludeActorsIdsRawJsonAndDetails()
    {
        var actorId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.Parse("2026-07-11T12:00:00Z");
        var governance = new Mock<INodeGovernanceManager>();
        var fees = new Mock<INodeFeeScheduleManager>();
        var store = new Mock<INodeTransparencyStore>();
        store.Setup(value => value.ListGovernanceAuditAsync(
                11,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success<IReadOnlyList<NodeGovernanceAudit>>(
            [
                new NodeGovernanceAudit
                {
                    Id = "node_governance_audit:private-governance-id",
                    Action = "ParametersUpdated",
                    ActorAvatarId = $"avatar:{actorId:N}",
                    PreviousVersion = 0,
                    NewVersion = 1,
                    AllowedChains = ["Algorand"],
                    Detail = "private-governance-detail",
                    OccurredAt = occurredAt,
                },
            ]));
        var destination = new NodeTreasuryDestinationResponse
        {
            Chain = "Simulated",
            Network = ChainNetwork.Devnet,
            Address = "sim:public-address",
            Version = 1,
            UpdatedByAvatarId = actorId,
            UpdatedAt = occurredAt,
        };
        store.Setup(value => value.ListTreasuryAuditAsync(
                11,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success<IReadOnlyList<NodeTreasuryAudit>>(
            [
                new NodeTreasuryAudit
                {
                    Id = "node_treasury_audit:private-treasury-id",
                    Action = "DestinationUpdated",
                    ActorAvatarId = $"avatar:{actorId:N}",
                    Chain = "Simulated",
                    Network = nameof(ChainNetwork.Devnet),
                    PreviousVersion = 0,
                    NewVersion = 1,
                    DestinationJson = JsonSerializer.Serialize(
                        destination,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    Detail = "private-treasury-detail",
                    OccurredAt = occurredAt,
                },
            ]));
        var manager = Build(governance.Object, fees.Object, store.Object);

        var governancePage = await manager.ListGovernanceAuditAsync(limit: 10);
        var treasuryPage = await manager.ListTreasuryAuditAsync(limit: 10);

        governancePage.IsError.Should().BeFalse();
        treasuryPage.IsError.Should().BeFalse();
        treasuryPage.Result!.Items.Should().ContainSingle()
            .Which.Destination.Address.Should().Be("sim:public-address");
        var json = JsonSerializer.Serialize(
            new { Governance = governancePage.Result, Treasury = treasuryPage.Result },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Contains(actorId.ToString("N"), StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        json.Contains("private-governance-id", StringComparison.Ordinal).Should().BeFalse();
        json.Contains("private-treasury-id", StringComparison.Ordinal).Should().BeFalse();
        json.Contains("private-governance-detail", StringComparison.Ordinal).Should().BeFalse();
        json.Contains("private-treasury-detail", StringComparison.Ordinal).Should().BeFalse();
        json.Contains("destinationJson", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    private static NodeTransparencyManager Build(
        INodeGovernanceManager governance,
        INodeFeeScheduleManager fees,
        INodeTransparencyStore store)
        => new(
            governance,
            fees,
            store,
            new NodeTransparencyCursorCodec(new EphemeralDataProtectionProvider()));

    private static NodeFeeAudit FeeAudit(
        string id,
        long version,
        string occurredAt,
        Guid actorId)
    {
        var snapshot = FeeSchedule(version, actorId);
        var previous = version > 1 ? FeeSchedule(version - 1, actorId) : null;
        return new NodeFeeAudit
        {
            Id = id,
            Action = "ScheduleUpdated",
            ActorAvatarId = $"avatar:{actorId:N}",
            PreviousVersion = Math.Max(0, version - 1),
            NewVersion = version,
            PreviousScheduleJson = previous is null
                ? null
                : JsonSerializer.Serialize(previous, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ScheduleJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Detail = "operator-only-detail",
            OccurredAt = DateTimeOffset.Parse(occurredAt),
        };
    }

    private static NodeFeeScheduleResponse FeeSchedule(long version, Guid actorId)
        => new()
        {
            Mint = new NodeFeeScheduleEntryResponse { FlatBaseUnits = version.ToString(), Bps = 10 },
            Transfer = new NodeFeeScheduleEntryResponse(),
            Swap = new NodeFeeScheduleEntryResponse(),
            QuestComplete = new NodeFeeScheduleEntryResponse(),
            FederationPublish = new NodeFeeScheduleEntryResponse(),
            Version = version,
            UpdatedByAvatarId = actorId,
            UpdatedAt = DateTimeOffset.Parse("2026-07-11T12:01:00Z"),
        };

    private static AZOAResult<T> Success<T>(T value)
        => new() { Result = value, Message = "Success" };
}
