// SPDX-License-Identifier: UNLICENSED
//
// These drive issuance the way the node's OWN on-ramp rail does — no acting tenant, and
// therefore an explicit ReserveDrawAuthority.NodeOperator. The reserve refuses a partition
// draw that carries neither an operator signal nor a resolvable tenant (OI-21), so a test
// that omitted the authority here would be testing a refusal, not the issuance mechanics.
// The tenant-driven door is proved in AllocationReserveTenantScopeTests.

using FluentAssertions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Tests.Allocation;

/// <summary>
/// W3.3 proofs for <see cref="AllocationKind.IssueUnits"/>: the units come FROM the
/// reserve, the recipient is made able to hold them before anything is sent, the node-side
/// partition refuses an overdraw before value moves, and nothing that reaches the tenant
/// names a chain.
/// </summary>
public class IssueUnitsAllocationTests
{
    // ── The recipient is prepared, and prepared FIRST ─────────────────────────

    [Fact]
    public async Task IssueUnits_PreparesTheRecipientBeforeDrawingAndBeforeMoving()
    {
        var harness = new AllocationTestHarness();
        harness.PreparerReturns(alreadyPrepared: false);
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "issue-1", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeFalse(result.Message);
        harness.Steps.Should().Equal("prepare", "draw", "broadcast");
    }

    [Fact]
    public async Task IssueUnits_PreparationFailure_MovesNothing()
    {
        var harness = new AllocationTestHarness();
        harness.PreparerFails("The recipient has not opted into ASA 77712345.");
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "issue-prep-fail", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        harness.Steps.Should().Equal("prepare");
        harness.Reserve.Verify(
            reserve => reserve.DrawAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.BlockchainOps.Verify(
            ops => ops.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()),
            Times.Never);
    }

    [Fact]
    public async Task IssueUnits_PreparationStillSettling_RefusesRatherThanSendingIntoAnUnreadyAccount()
    {
        var harness = new AllocationTestHarness();
        harness.PreparerReturns(alreadyPrepared: false, pendingConfirmation: true);
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "issue-pending", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        harness.Steps.Should().Equal("prepare");
        harness.BlockchainOps.Verify(
            ops => ops.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()),
            Times.Never);
    }

    [Fact]
    public async Task IssueUnits_AsksThePreparerForTheRecipientsOwnSigningContextAndTheReserveAsFeePayer()
    {
        var harness = new AllocationTestHarness();
        CustodialRecipientPreparation? captured = null;
        harness.Preparer
            .Setup(preparer => preparer.PrepareAsync(
                It.IsAny<CustodialRecipientPreparation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CustodialRecipientPreparation request, CancellationToken _) =>
            {
                captured = request;
                return AZOAResult<CustodialRecipientReadiness>.Success(
                    new CustodialRecipientReadiness(true, null, false, "0"));
            });
        var manager = harness.Build();

        await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "issue-ctx", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        captured.Should().NotBeNull();
        captured!.RecipientAddress.Should().Be(AllocationTestHarness.RecipientAddress);
        captured.FeePayerAddress.Should().Be(AllocationTestHarness.ReserveAddress);
        captured.RecipientSigningContext.IsResolvableUserContext.Should().BeTrue();
        captured.RecipientSigningContext.UserId.Should().Be(harness.UserId);
        captured.RecipientSigningContext.WalletId.Should().Be(harness.WalletId);
        captured.FeePayerSigningContext.IsPlatform.Should().BeTrue();
    }

    // ── The units come from the reserve, drawn from a named partition ─────────

    [Fact]
    public async Task IssueUnits_DrawsTheNetAmountFromTheNamedPartitionUnderTheAllocationLedgerKey()
    {
        var harness = new AllocationTestHarness();
        harness.QuoteFee(feeAmount: 10);
        ReserveDrawRequest? captured = null;
        harness.Reserve
            .Setup(reserve => reserve.DrawAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReserveDrawRequest request, CancellationToken _) =>
            {
                captured = request;
                return AZOAResult<ReserveDrawOutcome>.Success(new ReserveDrawOutcome(
                    request.AssetId, request.Partition, request.Kind, request.Amount,
                    new ReservePartitionCounters(1_000_000, request.Amount, 0, 0), 2, false));
            });
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(amount: "1000"),
            harness.CallerUserId, "issue-draw", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeFalse(result.Message);
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(ReserveDrawKind.Issue);
        captured.Partition.Should().Be(AllocationTestHarness.Partition);
        captured.AssetId.Should().Be(AllocationTestHarness.AssetId);
        // 1000 gross - 10 node fee: only the net leaves the reserve, so only the net is
        // what the partition has committed.
        captured.Amount.Should().Be(new System.Numerics.BigInteger(990));
        captured.OperationKey.Should().Be($"alloc:{AllocationTestHarness.ApiKeyGuid}:issue-draw");
        result.Result!.NetAmount.Should().Be("990");
        result.Result.NodeFeeAmount.Should().Be("10");
    }

    [Fact]
    public async Task IssueUnits_OversubscribedPartition_RefusesBeforeAnythingMoves()
    {
        var harness = new AllocationTestHarness();
        harness.DrawFails(
            "Partition 'contributor' would be oversubscribed: 1000 base units requested, 40 available.");
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "issue-oversub", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        harness.Steps.Should().Equal("prepare", "draw");
        harness.BlockchainOps.Verify(
            ops => ops.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()),
            Times.Never);
    }

    [Fact]
    public async Task IssueUnits_SendsFromTheReserveNotTheRecipientsWallet()
    {
        var harness = new AllocationTestHarness();
        var manager = harness.Build();

        await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "issue-sender", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        harness.BroadcastOp.Should().NotBeNull();
        // OI-25. An issuance is NOT an outbound transfer: it carries its own operation type so
        // the S6 scope map can demand units:issue for it and refuse transfer:sign, and so the
        // idempotency key is derived per recipient rather than collapsing onto the type alone.
        harness.BroadcastOp!.OperationType.Should().Be(AzoaScopes.IssueUnitsOperationType);
        harness.BroadcastOp.Parameters["WalletAddress"].Should().Be(AllocationTestHarness.ReserveAddress);
        ((ITransferOperation)harness.BroadcastOp).RecipientAddress
            .Should().Be(AllocationTestHarness.RecipientAddress);
        // WalletId null is what makes BlockchainOperationManager.BuildSigningContext
        // resolve the PLATFORM key: the units leave the reserve, not the person's wallet.
        harness.BroadcastOp.WalletId.Should().BeNull();
        harness.BroadcastOp.UserId.Should().Be(harness.UserId);
    }

    // ── Purpose ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueUnits_CarriesTheCallersPurposeOntoTheDurableOperation()
    {
        var harness = new AllocationTestHarness();
        var manager = harness.Build();

        await manager.AllocateAsync(
            harness.UserId,
            AllocationTestHarness.IssueUnitsRequest(purpose: "  milestone-2 contributor stake  "),
            harness.CallerUserId, "issue-purpose", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        harness.BroadcastOp.Should().NotBeNull();
        harness.BroadcastOp!.Parameters.Should().ContainKey("Purpose");
        harness.BroadcastOp.Parameters["Purpose"].Should().Be("milestone-2 contributor stake");
        harness.BroadcastOp.Parameters["ReservePartition"].Should().Be(AllocationTestHarness.Partition);
        harness.BroadcastOp.Parameters["AllocationKind"].Should().Be(nameof(AllocationKind.IssueUnits));
    }

    [Fact]
    public async Task IssueUnits_OverlongPurpose_IsRefusedBeforeAnySideEffect()
    {
        var harness = new AllocationTestHarness();
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId,
            AllocationTestHarness.IssueUnitsRequest(purpose: new string('p', 257)),
            harness.CallerUserId, "issue-long-purpose", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        harness.Steps.Should().BeEmpty();
    }

    // ── Inputs an issuance cannot be safely derived without ──────────────────

    [Theory]
    [InlineData(null, AllocationTestHarness.Partition, "issue-no-asset")]
    [InlineData(AllocationTestHarness.AssetId, null, "issue-no-partition")]
    [InlineData(AllocationTestHarness.AssetId, "not a partition!", "issue-bad-partition")]
    public async Task IssueUnits_MissingOrMalformedInputs_RefuseBeforeAnySideEffect(
        string? assetId, string? partition, string idempotencyKey)
    {
        var harness = new AllocationTestHarness();
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId,
            AllocationTestHarness.IssueUnitsRequest(assetId: assetId, partition: partition),
            harness.CallerUserId, idempotencyKey, AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        harness.Steps.Should().BeEmpty();
        harness.WalletManager.Verify(
            manager => manager.GenerateWalletAsync(
                It.IsAny<WalletGenerateRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()),
            Times.Never);
    }

    [Fact]
    public async Task IssueUnits_WithoutAnIdempotencyKey_Refuses()
    {
        var harness = new AllocationTestHarness();
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, clientIdempotencyKey: null, AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Idempotency-Key");
        harness.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task IssueUnits_WithoutAReserve_RefusesInsteadOfCreatingSupply()
    {
        var harness = new AllocationTestHarness();
        var manager = harness.Build(withReserve: false);

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "issue-no-reserve", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        harness.Steps.Should().BeEmpty();
        harness.BlockchainOps.Verify(
            ops => ops.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()),
            Times.Never);
    }

    // ── Replay ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueUnits_RedeliveredUnderTheSameKey_MovesOnce()
    {
        var harness = new AllocationTestHarness();
        var manager = harness.Build();

        var first = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "issue-once", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);
        var second = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "issue-once", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        first.IsError.Should().BeFalse(first.Message);
        second.IsError.Should().BeFalse(second.Message);
        second.Result!.Replayed.Should().BeTrue();
        harness.BlockchainOps.Verify(
            ops => ops.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()),
            Times.Once);
        harness.Reserve.Verify(
            reserve => reserve.DrawAsync(It.IsAny<ReserveDrawRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Retiring per-allocation supply creation for tenants ──────────────────

    [Fact]
    public async Task Mint_DrivenByATenant_IsRetiredAndPointsAtIssueUnits()
    {
        var harness = new AllocationTestHarness();
        var users = new Mock<AZOA.WebAPI.Interfaces.Stores.IUserStore>();
        users.Setup(store => store.GetByIdAsync(harness.UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser>
            {
                Result = new AZOA.WebAPI.Models.User
                {
                    Id = harness.UserId,
                    OwnerTenantId = harness.CallerUserId,
                },
            });
        var manager = new AZOA.WebAPI.Managers.AllocationManager(
            harness.ValueAccess.Object, harness.WalletManager.Object, harness.WalletStore.Object,
            harness.HolonStore.Object, harness.Nft.Object, harness.BlockchainOps.Object,
            harness.Idempotency, harness.NodeFees.Object,
            nodeGovernance: null, users: users.Object,
            reserve: harness.Reserve.Object, recipientPreparer: harness.Preparer.Object,
            feeSettlement: harness.FeeSettlement.Object, treasury: harness.Treasury.Object,
            providers: harness.Providers.Object);

        var result = await manager.AllocateAsync(
            harness.UserId,
            new AllocationRequest
            {
                Kind = AllocationKind.Mint,
                ChainType = AllocationTestHarness.ChainType,
                Amount = "100",
                Name = "Riverbank cleanup",
                AssetId = AllocationTestHarness.AssetId,
            },
            harness.CallerUserId, "tenant-mint", AllocationTestHarness.ApiKeyId,
            actingTenantId: harness.CallerUserId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("IssueUnits");
        harness.Steps.Should().BeEmpty();
        harness.BlockchainOps.Verify(
            ops => ops.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()),
            Times.Never);
    }

    // ── Quote before terms lock ──────────────────────────────────────────────

    [Fact]
    public async Task Preview_ReturnsTheExactFeeAndClaimsNothing()
    {
        var harness = new AllocationTestHarness();
        harness.QuoteFee(feeAmount: 25, scheduleVersion: 9);
        var manager = harness.Build();

        var request = AllocationTestHarness.IssueUnitsRequest(amount: "1000");
        request.PreviewOnly = true;

        var result = await manager.AllocateAsync(
            harness.UserId, request, harness.CallerUserId, "quote-1", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.GrossAmount.Should().Be("1000");
        result.Result.NodeFeeAmount.Should().Be("25");
        result.Result.NetAmount.Should().Be("975");
        result.Result.NodeFeeScheduleVersion.Should().Be(9);
        // The empty operation id is the machine-checkable proof that nothing happened.
        result.Result.OperationId.Should().Be(Guid.Empty);
        result.Result.WalletProvisioned.Should().BeFalse();
        harness.Steps.Should().BeEmpty();
        harness.Idempotency.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_DoesNotConsumeTheKeyTheRealAllocationWillNeed()
    {
        var harness = new AllocationTestHarness();
        var manager = harness.Build();

        var preview = AllocationTestHarness.IssueUnitsRequest();
        preview.PreviewOnly = true;
        await manager.AllocateAsync(
            harness.UserId, preview, harness.CallerUserId, "quote-then-issue", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        var real = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "quote-then-issue", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        real.IsError.Should().BeFalse(real.Message);
        real.Result!.Replayed.Should().BeFalse();
        real.Result.OperationId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task QuotedScheduleVersion_ThatHasMovedOn_RefusesRatherThanRepricing()
    {
        var harness = new AllocationTestHarness();
        harness.QuoteFee(feeAmount: 10, scheduleVersion: 12);
        var manager = harness.Build();

        var request = AllocationTestHarness.IssueUnitsRequest();
        request.ExpectedFeeScheduleVersion = 11;

        var result = await manager.AllocateAsync(
            harness.UserId, request, harness.CallerUserId, "issue-stale-terms",
            AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Re-quote");
        harness.Steps.Should().BeEmpty();
    }

    // ── Transfer fees no longer hard-deny ────────────────────────────────────

    [Fact]
    public async Task TransferFee_WithATreasuryDestination_IsAdmittedAsAnObligationInsteadOfDenied()
    {
        var harness = new AllocationTestHarness();
        harness.QuoteFee(feeAmount: 11);
        harness.WalletStore
            .Setup(store => store.GetByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { harness.Wallet() } });
        harness.Nft
            .Setup(nft => nft.TransferAsync(
                It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(),
                It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(new AZOAResult<IBlockchainOperation>
            {
                Result = new AZOA.WebAPI.Models.BlockchainOperation(),
            });
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId,
            new AllocationRequest
            {
                Kind = AllocationKind.Transfer,
                ChainType = AllocationTestHarness.ChainType,
                Amount = "100",
                Name = "Riverbank cleanup",
                AssetId = AllocationTestHarness.AssetId,
                AssetRecordId = Guid.NewGuid(),
            },
            harness.CallerUserId, "transfer-fee-ok", AllocationTestHarness.ApiKeyId);

        result.IsError.Should().BeFalse(result.Message);
        harness.FeeSettlement.Verify(
            settlement => settlement.PrepareAsync(
                It.Is<AZOA.WebAPI.Services.Governance.NodeFeeSettlementDraft>(draft =>
                    draft.FeeAmount == "11"
                    && draft.NetAmount == "89"
                    && draft.TreasuryAddress == AllocationTestHarness.TreasuryAddress
                    && draft.TreasuryDestinationVersion == 3),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TransferFee_WithNoTreasuryDestination_StillRefusesRatherThanShavingSilently()
    {
        var harness = new AllocationTestHarness();
        harness.QuoteFee(feeAmount: 11);
        var manager = harness.Build(withFeeSettlement: false);

        var result = await manager.AllocateAsync(
            harness.UserId,
            new AllocationRequest
            {
                Kind = AllocationKind.Transfer,
                ChainType = AllocationTestHarness.ChainType,
                Amount = "100",
                Name = "Riverbank cleanup",
                AssetId = AllocationTestHarness.AssetId,
                AssetRecordId = Guid.NewGuid(),
            },
            harness.CallerUserId, "transfer-fee-denied", AllocationTestHarness.ApiKeyId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("treasury settlement");
        harness.BlockchainOps.Verify(
            ops => ops.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()),
            Times.Never);
    }

    [Fact]
    public async Task IssueUnitsFee_NeedsNoTreasuryLegBecauseItNeverLeavesTheReserve()
    {
        var harness = new AllocationTestHarness();
        harness.QuoteFee(feeAmount: 10);
        var manager = harness.Build(withFeeSettlement: false);

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "issue-fee", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.NodeFeeAmount.Should().Be("10");
        result.Result.NetAmount.Should().Be("990");
    }

    // ── Nothing that reaches the tenant names a chain ─────────────────────────

    [Fact]
    public async Task RailFailure_IsMappedToAConsumerSafeMessageBeforeItCrossesTheTenantBoundary()
    {
        var harness = new AllocationTestHarness();
        harness.BroadcastFails(
            "The release recipient RECIPIENTADDRESSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA "
            + "has not opted into ASA 77712345. Algorand rejects a transfer to an account that "
            + "has not opted in, and the account must sign its own opt-in.");
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "issue-leak", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        ConsumerSafeErrors.LeaksRailDetail(result.Message).Should().BeFalse(
            "a sponsor must never see an address, an asset id, a chain name, or the words 'opt-in'");
        result.Message.Should().NotContain("ASA");
        result.Message.Should().NotContain("Algorand");
        result.Message.Should().NotContain("opt");
    }

    [Fact]
    public async Task ReserveRefusal_IsMappedToAConsumerSafeMessageToo()
    {
        var harness = new AllocationTestHarness();
        harness.DrawFails(
            "Partition 'contributor' would be oversubscribed on Algorand ASA 77712345: "
            + "1000 base units requested, 40 available.");
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "issue-oversub-safe", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        ConsumerSafeErrors.LeaksRailDetail(result.Message).Should().BeFalse();
        result.Code.Should().Be(ConsumerSafeErrorCodes.InsufficientFunds);
    }

    [Fact]
    public async Task UnconfirmedBroadcast_LeavesTheClaimInProgressAndSaysSoSafely()
    {
        var harness = new AllocationTestHarness();
        harness.BlockchainOps
            .Setup(ops => ops.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((IBlockchainOperation op, AZOARequest? _) =>
            {
                harness.Steps.Add("broadcast");
                return new AZOAResult<IBlockchainOperation> { Result = op };
            });
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId, "issue-unconfirmed", AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(ConsumerSafeErrorCodes.SettlementPending);
        ConsumerSafeErrors.LeaksRailDetail(result.Message).Should().BeFalse();

        var record = await harness.Idempotency.GetAsync(
            $"alloc:{AllocationTestHarness.ApiKeyGuid}:issue-unconfirmed", CancellationToken.None);
        record!.State.Should().Be(IdempotencyState.InProgress);
    }
}
