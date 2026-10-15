// SPDX-License-Identifier: UNLICENSED
// OI-20 / OI-21 — who may draw a declared reserve partition. Both draw seams ask the ONE
// predicate (ReservePartitionScope.Refuse), so both are proved here: the allocation seam,
// where the asset AND the partition arrive off the caller's body, and the hold seam, whose
// guard used to read "no tenant on the row" as "anyone may draw it".

using System.Numerics;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Tests.Allocation;

/// <summary>
/// The reserve is shared. A partition is a tenant's declared budget, and the platform's own
/// unit — which carries no tenant on its ledger row at all — is the operator's. These are
/// the proofs that neither is spendable by somebody else.
/// </summary>
public class AllocationReserveTenantScopeTests
{
    private static readonly Guid TenantOne = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantTwo = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // ── The allocation seam (OI-20) ──────────────────────────────────────────

    [Fact]
    public async Task IssueUnits_refuses_a_tenant_drawing_a_partition_another_tenant_registered()
    {
        var harness = new AllocationTestHarness();
        StubReserveLedger(harness, TenantOne.ToString("N"));
        var manager = BuildTenantDrivenManager(harness, TenantTwo);

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            callerUserId: TenantTwo, clientIdempotencyKey: "cross-tenant-issue",
            AllocationTestHarness.ApiKeyId, actingTenantId: TenantTwo);

        result.IsError.Should().BeTrue(
            "one tenant's declared budget is not another tenant's to draw out of the shared reserve");
        harness.Steps.Should().BeEmpty(
            "the refusal must precede the recipient preparation it would otherwise pay for");
        harness.Reserve.Verify(
            reserve => reserve.DrawAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.BlockchainOps.Verify(
            ops => ops.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()),
            Times.Never);
    }

    [Fact]
    public async Task IssueUnits_refuses_a_tenant_drawing_the_platform_units_own_partition()
    {
        var harness = new AllocationTestHarness();
        StubReserveLedger(harness, tenantId: null); // the platform unit: minted with no acting tenant
        var manager = BuildTenantDrivenManager(harness, TenantTwo);

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            callerUserId: TenantTwo, clientIdempotencyKey: "tenant-draws-platform-unit",
            AllocationTestHarness.ApiKeyId, actingTenantId: TenantTwo);

        result.IsError.Should().BeTrue(
            "an untenanted reserve row is the node's own supply, not a supply every tenant may spend");
        harness.Steps.Should().BeEmpty();
        harness.Reserve.Verify(
            reserve => reserve.DrawAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task IssueUnits_lets_the_operator_draw_the_platform_units_partition()
    {
        var harness = new AllocationTestHarness();
        StubReserveLedger(harness, tenantId: null);
        var manager = harness.Build();

        // The node's own path — OnRampSettlementHandler is the production caller — and it
        // now carries a POSITIVE operator signal. The missing acting tenant grants nothing
        // on its own; see the sibling test below.
        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "operator-draws-platform-unit", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeFalse(result.Message);
        harness.Steps.Should().Equal("prepare", "draw", "broadcast");
    }

    [Fact]
    public async Task IssueUnits_refuses_a_caller_that_has_neither_an_operator_signal_nor_a_tenant()
    {
        // OI-21, re-opened. The old predicate read "no acting tenant" as "the operator", and
        // a user-driven quest run reaches the draw seams with exactly that: ActionManager
        // and ActionNodeStepHandler both build the node context from run.ActingTenantId,
        // which is null for a run nobody's tenant drove.
        var harness = new AllocationTestHarness();
        StubReserveLedger(harness, tenantId: null);
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "no-signal-no-tenant", AllocationTestHarness.ApiKeyId);

        result.IsError.Should().BeTrue(
            "the absence of a tenant is not an operator signal");
        harness.Steps.Should().BeEmpty();
        harness.Reserve.Verify(
            reserve => reserve.DrawAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the same id in the record-link shape SurrealDB hands back
    public async Task IssueUnits_lets_a_tenant_draw_the_partition_of_the_asset_it_registered(
        bool asRecordLink)
    {
        var harness = new AllocationTestHarness();
        var owner = TenantOne.ToString("N");
        StubReserveLedger(harness, asRecordLink ? "user:" + owner : owner);
        var manager = BuildTenantDrivenManager(harness, TenantOne);

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            callerUserId: TenantOne, clientIdempotencyKey: "own-partition-" + asRecordLink,
            AllocationTestHarness.ApiKeyId, actingTenantId: TenantOne);

        result.IsError.Should().BeFalse(result.Message);
        harness.Steps.Should().Equal("prepare", "draw", "broadcast");
    }

    // ── The hold seam (OI-21) ────────────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_refuses_a_tenant_drawing_the_platform_units_own_partition()
    {
        var world = new HoldWorld(reserveRowTenantId: null);

        var opened = await world.OpenAsync(actingTenantId: TenantTwo);

        opened.IsError.Should().BeTrue(
            "the platform unit's declared partitions are the operator's, not every tenant's");
        world.Ledger.CreateCalls.Should().Be(0, "a refused hold must leave no ledger row behind");
        world.Reserve.Verify(
            reserve => reserve.DrawAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OpenAsync_lets_the_operator_draw_the_platform_units_partition()
    {
        var world = new HoldWorld(reserveRowTenantId: null);

        var opened = await world.OpenAsync(
            actingTenantId: null, drawAuthority: ReserveDrawAuthority.NodeOperator);

        opened.IsError.Should().BeFalse(opened.Message);
        world.Reserve.Verify(
            reserve => reserve.DrawAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OpenAsync_refuses_a_hold_that_has_neither_an_operator_signal_nor_a_tenant()
    {
        // The shape HoldNodeHandler forwards for a user-driven run, with cfg.AssetId and
        // cfg.ReservePartition both taken from author-supplied action config.
        var world = new HoldWorld(reserveRowTenantId: null);

        var opened = await world.OpenAsync(actingTenantId: null);

        opened.IsError.Should().BeTrue("a run with no tenant is not the node operator");
        world.Ledger.CreateCalls.Should().Be(0);
        world.Reserve.Verify(
            reserve => reserve.DrawAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OpenAsync_refuses_a_tenant_drawing_a_partition_another_tenant_registered()
    {
        var world = new HoldWorld(reserveRowTenantId: TenantOne.ToString("N"));

        var opened = await world.OpenAsync(actingTenantId: TenantTwo);

        opened.IsError.Should().BeTrue();
        world.Ledger.CreateCalls.Should().Be(0);
        world.Reserve.Verify(
            reserve => reserve.DrawAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    /// <summary>The reserve's own row for the harness asset, owned by <paramref name="tenantId"/> or by nobody.</summary>
    private static void StubReserveLedger(AllocationTestHarness harness, string? tenantId)
        => harness.Reserve
            .Setup(reserve => reserve.GetLedgerAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<PlatformReserveLedger>.Success(new PlatformReserveLedger
            {
                Id = "platform_reserve_ledger:test",
                AssetId = AllocationTestHarness.AssetId,
                Chain = AllocationTestHarness.ChainType,
                Network = ChainNetwork.Devnet.ToString(),
                ReserveAddress = AllocationTestHarness.ReserveAddress,
                TenantId = tenantId,
                UnitName = "STAKE",
                TotalSupply = "1000000",
            }));

    /// <summary>
    /// The manager as a tenant reaches it: the acting tenant must be the caller, and the
    /// target must be a user that tenant provisioned, or <c>AllocateAsync</c> refuses before
    /// the partition question is ever asked.
    /// </summary>
    private static AllocationManager BuildTenantDrivenManager(AllocationTestHarness harness, Guid tenantId)
    {
        var users = new Mock<IUserStore>();
        users
            .Setup(store => store.GetByIdAsync(harness.UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser>
            {
                Result = new AZOA.WebAPI.Models.User { Id = harness.UserId, OwnerTenantId = tenantId },
            });

        return new AllocationManager(
            harness.ValueAccess.Object, harness.WalletManager.Object, harness.WalletStore.Object,
            harness.HolonStore.Object, harness.Nft.Object, harness.BlockchainOps.Object,
            harness.Idempotency, harness.NodeFees.Object,
            nodeGovernance: null,
            users: users.Object,
            reserve: harness.Reserve.Object,
            recipientPreparer: harness.Preparer.Object,
            feeSettlement: harness.FeeSettlement.Object,
            treasury: harness.Treasury.Object,
            providers: harness.Providers.Object);
    }

    /// <summary>A hold service over a mocked reserve and an in-memory hold ledger.</summary>
    private sealed class HoldWorld
    {
        private const string Chain = "Algorand";
        private const string AssetId = "1001";
        private const string Partition = "contributor";
        private const string ReserveAddress = "RESERVEADDRESSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        private static readonly Guid Beneficiary = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

        private readonly PlatformHoldService _holds;

        public Mock<IPlatformReserve> Reserve { get; } = new();

        public InMemoryHoldLedgerStore Ledger { get; } = new();

        public HoldWorld(string? reserveRowTenantId)
        {
            Reserve
                .Setup(reserve => reserve.GetAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<ReserveAccount>.Success(
                    new ReserveAccount(Chain, ReserveAddress, IsFeePayer: true)));

            Reserve
                .Setup(reserve => reserve.GetLedgerAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<PlatformReserveLedger>.Success(new PlatformReserveLedger
                {
                    Id = "platform_reserve_ledger:hold-test",
                    AssetId = AssetId,
                    Chain = Chain,
                    Network = ChainNetwork.Devnet.ToString(),
                    ReserveAddress = ReserveAddress,
                    TenantId = reserveRowTenantId,
                    UnitName = "STAKE",
                    TotalSupply = "1000000",
                }));

            Reserve
                .Setup(reserve => reserve.DrawAsync(
                    It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ReserveDrawRequest request, CancellationToken _) =>
                    AZOAResult<ReserveDrawOutcome>.Success(new ReserveDrawOutcome(
                        request.AssetId, request.Partition, request.Kind, request.Amount,
                        new ReservePartitionCounters(
                            new BigInteger(1_000_000), BigInteger.Zero, request.Amount, BigInteger.Zero),
                        Version: 2, Replayed: false)));

            var providers = new Mock<IBlockchainProviderFactory>();
            providers.SetupGet(factory => factory.DefaultNetwork).Returns(ChainNetwork.Devnet);

            _holds = new PlatformHoldService(
                Reserve.Object,
                Mock.Of<ICustodialRecipientPreparer>(),
                providers.Object,
                Mock.Of<IWalletStore>(),
                Ledger,
                new ReserveOptions(),
                Mock.Of<ILogger<PlatformHoldService>>());
        }

        public Task<AZOAResult<HoldOutcome>> OpenAsync(
            Guid? actingTenantId, ReserveDrawAuthority? drawAuthority = null) =>
            _holds.OpenAsync(new HoldOpenRequest(
                new HoldReference(Chain, "hold-scope"),
                AssetId,
                new BigInteger(100),
                HoldFundingSource.Reserve,
                Beneficiary,
                ReservePartition: Partition,
                FunderUserId: Guid.Empty,
                ReleaseNotBefore: null,
                AutoRelease: true,
                ActingTenantId: actingTenantId,
                Purpose: null,
                DrawAuthority: drawAuthority));
    }

    /// <summary>Claim-once hold ledger; counts creates so "nothing was held" is checkable.</summary>
    private sealed class InMemoryHoldLedgerStore : IHoldLedgerStore
    {
        private readonly Dictionary<string, HoldLedger> _rows = new(StringComparer.Ordinal);

        public int CreateCalls { get; private set; }

        public Task<AZOAResult<HoldLedger?>> GetAsync(string recordId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<HoldLedger?>.Success(
                _rows.TryGetValue(recordId, out var row) ? row : null));

        public Task<AZOAResult<HoldLedgerClaim>> CreateIfAbsentAsync(
            HoldLedger row, CancellationToken ct = default)
        {
            CreateCalls++;
            if (_rows.TryGetValue(row.Id, out var existing))
                return Task.FromResult(AZOAResult<HoldLedgerClaim>.Success(new HoldLedgerClaim(existing, false)));

            _rows[row.Id] = row;
            return Task.FromResult(AZOAResult<HoldLedgerClaim>.Success(new HoldLedgerClaim(row, true)));
        }

        public Task<AZOAResult<HoldLedger>> UpdateIfVersionAsync(
            HoldLedger row, long expectedVersion, CancellationToken ct = default)
        {
            _rows[row.Id] = row;
            return Task.FromResult(AZOAResult<HoldLedger>.Success(row));
        }

        public Task<AZOAResult<IReadOnlyList<HoldLedger>>> ListForUserAsync(
            string userId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<HoldLedger>>.Success(_rows.Values.ToList()));

        public Task<AZOAResult<IReadOnlyList<HoldLedger>>> ListDueAsync(
            string chain, string network, DateTimeOffset asOf, int limit, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<HoldLedger>>.Success(new List<HoldLedger>()));
    }
}
