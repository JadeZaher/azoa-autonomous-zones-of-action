// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Governance;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// Replay-safety + fail-closed + provision-if-absent + IDOR proofs for the
/// fiat-settlement allocation seam. Uses an in-memory <see cref="IIdempotencyStore"/>
/// that faithfully reproduces claim-once / replay-cached semantics so the
/// "mint/transfer happens exactly once" assertions are real, not mock-scripted.
/// </summary>
public class AllocationManagerTests
{
    private const string ApiKeyId = "11111111-1111-1111-1111-111111111111";
    private const string ChainType = "Algorand";

    private readonly Mock<IKycGateService> _kyc = new();
    private readonly Mock<IWalletManager> _walletManager = new();
    private readonly Mock<IWalletStore> _walletStore = new();
    private readonly Mock<INftManager> _nft = new();
    private readonly Mock<IBlockchainOperationManager> _blockchainOps = new();
    private readonly Mock<INodeFeeScheduleManager> _nodeFees = new();
    private readonly InMemoryIdempotencyStore _idempotency = new();

    private readonly Guid _avatarId = Guid.NewGuid();
    private readonly Guid _callerAvatarId = Guid.NewGuid();

    public AllocationManagerTests()
    {
        _nodeFees
            .Setup(m => m.QuoteAsync(
                It.IsAny<NodeFeeOperation>(),
                It.IsAny<ulong>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((NodeFeeOperation operation, ulong gross, CancellationToken _) =>
                new AZOAResult<NodeFeeQuoteResponse>
                {
                    Result = new NodeFeeQuoteResponse
                    {
                        Operation = operation,
                        GrossAmount = gross.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        FeeAmount = "0",
                        NetAmount = gross.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ScheduleVersion = 0,
                    },
                    Message = "Success",
                });
    }

    // value-path-wiring C2: the allocation now broadcasts through
    // IBlockchainOperationManager.ExecuteAsync (it no longer treats NftManager's
    // upsert-only op as the value-bearing result). The default broadcast returns a
    // confirmed op carrying a TxHash so the alloc idempotency key can Complete; a
    // dedicated test overrides this to assert the no-TxHash → stay-InProgress path.
    private AllocationManager BuildManager(
        INodeGovernanceGuard? nodeGovernance = null,
        INodeFeeScheduleManager? nodeFees = null)
    {
        SetupBroadcastReturnsTxHash();
        return new(
            _kyc.Object, _walletManager.Object, _walletStore.Object,
            _nft.Object, _blockchainOps.Object, _idempotency,
            nodeFees ?? _nodeFees.Object, nodeGovernance);
    }

    private void SetupBroadcastReturnsTxHash(string txHash = "algo_tx_alloc")
        => _blockchainOps
            .Setup(b => b.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((IBlockchainOperation op, AZOARequest? _) =>
            {
                op.Parameters["TxHash"] = txHash;
                op.Status = OperationStatus.Minted;
                return new AZOAResult<IBlockchainOperation> { Result = op };
            });

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ApproveKyc(Guid avatarId)
        => _kyc.Setup(k => k.RequireVerifiedAsync(avatarId))
               .ReturnsAsync(new AZOAResult<bool> { Result = true, Message = "Success" });

    private void DenyKyc(Guid avatarId)
        => _kyc.Setup(k => k.RequireVerifiedAsync(avatarId))
               .ReturnsAsync(new AZOAResult<bool>
               {
                   IsError = true,
                   Result = false,
                   Message = $"{KycAuthorizationError.Forbidden}{KycAuthorizationError.VerificationRequiredMessage}"
               });

    private static IWallet WalletFor(Guid avatarId, string chain) =>
        Mock.Of<IWallet>(w =>
            w.Id == Guid.NewGuid() &&
            w.AvatarId == avatarId &&
            w.ChainType == chain &&
            w.Address == "ALGOTESTADDRESS" &&
            w.WalletType == WalletType.Platform);

    private void HasNoWallet(Guid avatarId)
        => _walletStore.Setup(s => s.GetByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = Array.Empty<IWallet>() });

    private void HasWallet(Guid avatarId, IWallet wallet)
        => _walletStore.Setup(s => s.GetByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { wallet } });

    private void SetupGenerate(Guid avatarId, IWallet wallet)
        => _walletManager.Setup(m => m.GenerateWalletAsync(
                It.IsAny<WalletGenerateRequest>(), avatarId, It.IsAny<AZOARequest?>()))
            .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });

    private void SetupMintSucceeds()
        => _nft.Setup(n => n.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
               .ReturnsAsync(new AZOAResult<IBlockchainOperation>
               {
                   Result = Mock.Of<IBlockchainOperation>(o => o.Id == Guid.NewGuid())
               });

    private void SetupFeeQuote(
        NodeFeeOperation operation,
        ulong grossAmount,
        ulong feeAmount,
        long scheduleVersion)
        => _nodeFees
            .Setup(m => m.QuoteAsync(operation, grossAmount, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeFeeQuoteResponse>
            {
                Result = new NodeFeeQuoteResponse
                {
                    Operation = operation,
                    GrossAmount = grossAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    FeeAmount = feeAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    NetAmount = (grossAmount - feeAmount).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ScheduleVersion = scheduleVersion,
                },
                Message = "Success",
            });

    // value-path-wiring H4/D5: the allocation amount is a base-unit INTEGER string
    // (the provider surface is ulong). The prior "100.00" decimal data predates the
    // strict integer parse; it is now an integer so the broadcast amount round-trips
    // without truncation. (Idempotency/replay/IDOR assertions are unaffected.)
    private static AllocationRequest MintRequest() => new()
    {
        Kind = AllocationKind.Mint,
        ChainType = ChainType,
        Amount = "100",
        Name = "Project Alpha",
        AssetId = "PRJALPHA"
    };

    [Fact]
    public async Task AllocateAsync_NodeGovernanceDisallowedChain_RejectsBeforeClaimOrSideEffects()
    {
        var guard = new NodeGovernanceGuard(Options.Create(new NodeGovernanceOptions
        {
            AllowedChains = new[] { "Solana" }
        }));
        var manager = BuildManager(guard);

        var result = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "blocked", ApiKeyId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Node governance disallows allocation:Mint on chain 'Algorand'");
        _kyc.Verify(k => k.RequireVerifiedAsync(It.IsAny<Guid>()), Times.Never);
        _walletStore.Verify(s => s.GetByAvatarAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _nft.Verify(n => n.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()), Times.Never);
        _blockchainOps.Verify(b => b.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()), Times.Never);
        (await _idempotency.GetAsync($"alloc:{ApiKeyId}:blocked", CancellationToken.None)).Should().BeNull();
    }

    // ── Idempotency ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AllocateAsync_DuplicateKey_ReplaysOriginalAndMintsExactlyOnce()
    {
        ApproveKyc(_avatarId);
        var wallet = WalletFor(_avatarId, ChainType);
        HasWallet(_avatarId, wallet);
        SetupMintSucceeds();
        var manager = BuildManager();

        var first = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_stable_key", ApiKeyId);
        var second = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_stable_key", ApiKeyId);

        first.IsError.Should().BeFalse();
        first.Result!.Replayed.Should().BeFalse();

        second.IsError.Should().BeFalse();
        second.Result!.Replayed.Should().BeTrue();
        second.Result.OperationId.Should().Be(first.Result.OperationId);

        // The irreversible effect ran exactly once across the duplicate calls.
        _nft.Verify(n => n.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Once);
    }

    [Fact]
    public async Task AllocateAsync_AbsentClientKey_UsesDeterministicContentKey_StillDedupes()
    {
        ApproveKyc(_avatarId);
        var wallet = WalletFor(_avatarId, ChainType);
        HasWallet(_avatarId, wallet);
        SetupMintSucceeds();
        var manager = BuildManager();

        // No client Idempotency-Key on either call ⇒ deterministic content key.
        var first = await manager.AllocateAsync(_avatarId, MintRequest(), _callerAvatarId, null, ApiKeyId);
        var second = await manager.AllocateAsync(_avatarId, MintRequest(), _callerAvatarId, null, ApiKeyId);

        first.IsError.Should().BeFalse();
        second.Result!.Replayed.Should().BeTrue();
        _nft.Verify(n => n.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Once);
    }

    [Fact]
    public async Task AllocateAsync_DuplicateKey_DoesNotRequoteOrRebroadcastAndPreservesFeeVersion()
    {
        ApproveKyc(_avatarId);
        HasWallet(_avatarId, WalletFor(_avatarId, ChainType));
        SetupMintSucceeds();
        SetupFeeQuote(NodeFeeOperation.Mint, 100, 7, 17);
        var manager = BuildManager();

        var first = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_fee_replay", ApiKeyId);
        var replay = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_fee_replay", ApiKeyId);

        first.IsError.Should().BeFalse(first.Message);
        first.Result!.NodeFeeScheduleVersion.Should().Be(17);
        first.Result.NetAmount.Should().Be("93");
        replay.IsError.Should().BeFalse(replay.Message);
        replay.Result!.Replayed.Should().BeTrue();
        replay.Result.NodeFeeScheduleVersion.Should().Be(17);
        replay.Result.NetAmount.Should().Be("93");
        _nodeFees.Verify(
            m => m.QuoteAsync(NodeFeeOperation.Mint, 100, It.IsAny<CancellationToken>()),
            Times.Once);
        _nft.Verify(
            n => n.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Once);
        _blockchainOps.Verify(
            b => b.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()),
            Times.Once);
    }

    // ── value-path-wiring C2 + H1: real broadcast + crash-replay exactly-once ──

    [Fact]
    public async Task AllocateAsync_BroadcastsThroughExecuteAsync_AndPersistsIdempotencyKeyOnOpRow()
    {
        ApproveKyc(_avatarId);
        var wallet = WalletFor(_avatarId, ChainType);
        HasWallet(_avatarId, wallet);
        SetupMintSucceeds();

        IBlockchainOperation? broadcastOp = null;
        _blockchainOps
            .Setup(b => b.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((IBlockchainOperation op, AZOARequest? _) =>
            {
                broadcastOp = op;
                op.Parameters["TxHash"] = "algo_tx_real";
                op.Status = OperationStatus.Minted;
                return new AZOAResult<IBlockchainOperation> { Result = op };
            });
        var manager = new AllocationManager(
            _kyc.Object, _walletManager.Object, _walletStore.Object,
            _nft.Object, _blockchainOps.Object, _idempotency, _nodeFees.Object);

        var result = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_broadcast", ApiKeyId);

        // C2: the allocation actually drove the real broadcast path and the key was
        // Completed only with the recorded TxHash.
        result.IsError.Should().BeFalse(result.Message);
        _blockchainOps.Verify(
            b => b.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()), Times.Once);
        broadcastOp.Should().NotBeNull();
        broadcastOp!.Parameters.Should().ContainKey("TxHash");

        // H1: the alloc:{apiKeyId}:… key is persisted on the op row so reconciliation
        // can release an orphaned claim from chain truth (bridge precedent).
        broadcastOp.Parameters.Should().ContainKey("IdempotencyKey");
        broadcastOp.Parameters["IdempotencyKey"].Should().Be(result.Result!.IdempotencyKey);
        broadcastOp.Parameters["IdempotencyKey"].Should().StartWith($"alloc:{ApiKeyId}:");

        // The key settled Completed (a TxHash was recorded).
        var record = await _idempotency.GetAsync(result.Result!.IdempotencyKey, CancellationToken.None);
        record!.State.Should().Be(IdempotencyState.Completed);
    }

    [Fact]
    public async Task AllocateAsync_CrashBetweenBroadcastAndComplete_DuplicateDoesNotReMint()
    {
        // Models a crash AFTER the provider minted (op has a TxHash) but BEFORE the
        // alloc idempotency key was Completed: the claim is stuck InProgress. A
        // duplicate must NOT re-broadcast; reconciliation settles the orphan from the
        // persisted op.Parameters["IdempotencyKey"].
        ApproveKyc(_avatarId);
        var wallet = WalletFor(_avatarId, ChainType);
        HasWallet(_avatarId, wallet);
        SetupMintSucceeds();

        var mintCalls = 0;
        IBlockchainOperation? broadcastOp = null;
        _blockchainOps
            .Setup(b => b.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((IBlockchainOperation op, AZOARequest? _) =>
            {
                Interlocked.Increment(ref mintCalls);
                broadcastOp = op;
                op.Parameters["TxHash"] = "algo_tx_crash";
                op.Status = OperationStatus.Minted;
                return new AZOAResult<IBlockchainOperation> { Result = op };
            });

        // CompleteAsync is a no-op (the crash) so the key stays InProgress after the
        // first call — exactly the orphaned-claim state.
        var crashStore = new CrashAfterBroadcastIdempotencyStore();
        var manager = new AllocationManager(
            _kyc.Object, _walletManager.Object, _walletStore.Object,
            _nft.Object, _blockchainOps.Object, crashStore, _nodeFees.Object);

        var first = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_crash", ApiKeyId);
        var second = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_crash", ApiKeyId);

        // The provider mint ran EXACTLY ONCE across the duplicate calls.
        mintCalls.Should().Be(1, "the crashed claim must not re-broadcast on the duplicate");
        _blockchainOps.Verify(
            b => b.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()), Times.Once);

        // The second call replays the still-InProgress claim (not a false success).
        second.IsError.Should().BeTrue("a duplicate against an InProgress claim replays in-progress");
        second.Message.Should().Contain("in progress");

        // H1: the orphaned claim is recoverable — the op row carries the alloc key.
        broadcastOp!.Parameters.Should().ContainKey("IdempotencyKey");
        broadcastOp.Parameters["IdempotencyKey"].Should().StartWith($"alloc:{ApiKeyId}:");
        var orphan = await crashStore.GetAsync(broadcastOp.Parameters["IdempotencyKey"], CancellationToken.None);
        orphan!.State.Should().Be(IdempotencyState.InProgress,
            "the claim stays InProgress until reconciliation settles it from the persisted key + chain truth");
    }

    [Fact]
    public async Task AllocateAsync_UnexpectedEffectFailure_BubblesAndDuplicateDoesNotRetryEffect()
    {
        ApproveKyc(_avatarId);
        var wallet = WalletFor(_avatarId, ChainType);
        HasWallet(_avatarId, wallet);
        SetupMintSucceeds();
        _blockchainOps
            .Setup(b => b.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()))
            .ThrowsAsync(new InvalidOperationException("ambiguous provider failure"));
        var manager = new AllocationManager(
            _kyc.Object, _walletManager.Object, _walletStore.Object,
            _nft.Object, _blockchainOps.Object, _idempotency, _nodeFees.Object);

        Func<Task> first = async () => await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "ambiguous", ApiKeyId);
        await first.Should().ThrowAsync<InvalidOperationException>();

        var replay = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "ambiguous", ApiKeyId);
        replay.IsError.Should().BeTrue();
        replay.Message.Should().Contain("in progress");
        _blockchainOps.Verify(
            b => b.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()), Times.Once);
        var record = await _idempotency.GetAsync(
            $"alloc:{ApiKeyId}:ambiguous", CancellationToken.None);
        record!.State.Should().Be(IdempotencyState.InProgress);
    }

    // ── value-path-wiring H4: amount widening + rejection-before-broadcast ──────

    [Fact]
    public async Task AllocateAsync_AmountAboveIntMax_ReachesProviderAsCorrectUlong()
    {
        // 5_000_000_000 > int.MaxValue (~2.147e9): an int surface would truncate.
        const string bigAmount = "5000000000";
        ApproveKyc(_avatarId);
        var wallet = WalletFor(_avatarId, ChainType);
        HasWallet(_avatarId, wallet);
        SetupMintSucceeds();

        IBlockchainOperation? broadcastOp = null;
        _blockchainOps
            .Setup(b => b.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((IBlockchainOperation op, AZOARequest? _) =>
            {
                broadcastOp = op;
                op.Parameters["TxHash"] = "algo_tx_big";
                op.Status = OperationStatus.Minted;
                return new AZOAResult<IBlockchainOperation> { Result = op };
            });
        var manager = new AllocationManager(
            _kyc.Object, _walletManager.Object, _walletStore.Object,
            _nft.Object, _blockchainOps.Object, _idempotency, _nodeFees.Object);

        var request = new AllocationRequest
        {
            Kind = AllocationKind.Mint,
            ChainType = ChainType,
            Amount = bigAmount,
            Name = "Big Grant",
            AssetId = "BIG"
        };
        var result = await manager.AllocateAsync(_avatarId, request, _callerAvatarId, "pi_big", ApiKeyId);

        result.IsError.Should().BeFalse(result.Message);
        broadcastOp.Should().NotBeNull();
        broadcastOp!.Parameters["Amount"].Should().Be(bigAmount,
            "the full ulong amount must round-trip to the op without int truncation");
        ulong.Parse(broadcastOp.Parameters["Amount"]).Should().Be(5_000_000_000UL);
    }

    [Fact]
    public async Task AllocateAsync_MintFee_UsesNetTypedAmountAndStampsQuoteMetadata()
    {
        ApproveKyc(_avatarId);
        HasWallet(_avatarId, WalletFor(_avatarId, ChainType));
        SetupMintSucceeds();
        SetupFeeQuote(NodeFeeOperation.Mint, 100, 7, 4);
        var manager = BuildManager();

        IBlockchainOperation? broadcastOp = null;
        _blockchainOps
            .Setup(b => b.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync((IBlockchainOperation op, AZOARequest? _) =>
            {
                broadcastOp = op;
                op.Parameters["TxHash"] = "algo_tx_fee_mint";
                op.Status = OperationStatus.Minted;
                return new AZOAResult<IBlockchainOperation> { Result = op };
            });

        var result = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_fee_mint", ApiKeyId);

        result.IsError.Should().BeFalse(result.Message);
        broadcastOp.Should().BeAssignableTo<IMintOperation>();
        ((IMintOperation)broadcastOp!).Amount.Should().Be(93UL);
        broadcastOp.Parameters["GrossAmount"].Should().Be("100");
        broadcastOp.Parameters["NodeFeeAmount"].Should().Be("7");
        broadcastOp.Parameters["NetAmount"].Should().Be("93");
        broadcastOp.Parameters["NodeFeeScheduleVersion"].Should().Be("4");
        result.Result!.GrossAmount.Should().Be("100");
        result.Result.NodeFeeAmount.Should().Be("7");
        result.Result.NetAmount.Should().Be("93");
        result.Result.NodeFeeScheduleVersion.Should().Be(4);
    }

    [Fact]
    public async Task AllocateAsync_TransferFeeWithoutTreasurySettlement_FailsBeforeSideEffects()
    {
        ApproveKyc(_avatarId);
        SetupFeeQuote(NodeFeeOperation.Transfer, 100, 11, 6);
        var manager = BuildManager();
        var request = new AllocationRequest
        {
            Kind = AllocationKind.Transfer,
            ChainType = ChainType,
            Amount = "100",
            AssetId = "PRJALPHA",
            AssetRecordId = Guid.NewGuid(),
            Name = "Project Alpha",
        };

        var result = await manager.AllocateAsync(
            _avatarId, request, _callerAvatarId, "pi_fee_transfer", ApiKeyId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("treasury settlement");
        _walletStore.Verify(
            s => s.GetByAvatarAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _nft.Verify(
            n => n.TransferAsync(It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Never);
        _blockchainOps.Verify(
            b => b.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()),
            Times.Never);
    }

    [Fact]
    public async Task AllocateAsync_FeeQuoteFailure_FailsClaimBeforeWalletNftOrBroadcast()
    {
        ApproveKyc(_avatarId);
        _nodeFees
            .Setup(m => m.QuoteAsync(NodeFeeOperation.Mint, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<NodeFeeQuoteResponse>
            {
                IsError = true,
                Message = "Node fee schedule unavailable: store offline.",
            });
        var manager = BuildManager();

        var result = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_fee_unavailable", ApiKeyId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Node fee schedule unavailable");
        _walletStore.Verify(
            s => s.GetByAvatarAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _walletManager.Verify(
            m => m.GenerateWalletAsync(It.IsAny<WalletGenerateRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()),
            Times.Never);
        _nft.Verify(
            n => n.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Never);
        _blockchainOps.Verify(
            b => b.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()),
            Times.Never);
        var claim = await _idempotency.GetAsync(
            $"alloc:{ApiKeyId}:pi_fee_unavailable", CancellationToken.None);
        claim!.State.Should().Be(IdempotencyState.Failed);
    }

    [Theory]
    [InlineData("abc")]      // non-numeric
    [InlineData("100.00")]   // non-integer (base units must be integral)
    [InlineData("-5")]       // negative
    [InlineData("99999999999999999999999999")] // overflows ulong
    public async Task AllocateAsync_InvalidAmount_RejectedBeforeBroadcast_KeyFailedNotLeaked(string badAmount)
    {
        ApproveKyc(_avatarId);
        var wallet = WalletFor(_avatarId, ChainType);
        HasWallet(_avatarId, wallet);
        SetupMintSucceeds();
        var manager = new AllocationManager(
            _kyc.Object, _walletManager.Object, _walletStore.Object,
            _nft.Object, _blockchainOps.Object, _idempotency, _nodeFees.Object);

        var request = new AllocationRequest
        {
            Kind = AllocationKind.Mint,
            ChainType = ChainType,
            Amount = badAmount,
            Name = "Bad",
            AssetId = "BAD"
        };
        var result = await manager.AllocateAsync(_avatarId, request, _callerAvatarId, "pi_bad", ApiKeyId);

        result.IsError.Should().BeTrue($"'{badAmount}' is not a valid base-unit amount");

        // No broadcast happened (rejected before ExecuteAsync).
        _blockchainOps.Verify(
            b => b.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()), Times.Never);

        // The idempotency key is FAILED (terminal) — not leaked as a perpetual
        // InProgress duplicate.
        var record = await _idempotency.GetAsync($"alloc:{ApiKeyId}:pi_bad", CancellationToken.None);
        record!.State.Should().Be(IdempotencyState.Failed);
    }

    // ── KYC fail-closed ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("pending")]
    [InlineData("rejected")]
    [InlineData("unknown")]
    public async Task AllocateAsync_KycNotApproved_RejectsAndNeverMints(string kycState)
    {
        DenyKyc(_avatarId);
        HasNoWallet(_avatarId);
        SetupMintSucceeds();
        var manager = BuildManager();

        var result = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, $"pi_{kycState}", ApiKeyId);

        result.IsError.Should().BeTrue($"a {kycState} KYC avatar must be rejected fail-closed");
        result.Message.Should().StartWith(KycAuthorizationError.Forbidden);

        // No value-bearing side effect: never minted, never provisioned.
        _nft.Verify(n => n.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()),
            Times.Never);
        _walletManager.Verify(m => m.GenerateWalletAsync(
            It.IsAny<WalletGenerateRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()), Times.Never);
    }

    // ── Provision-if-absent ──────────────────────────────────────────────────

    [Fact]
    public async Task AllocateAsync_NoWallet_GeneratesExactlyOnce()
    {
        ApproveKyc(_avatarId);
        HasNoWallet(_avatarId);
        SetupGenerate(_avatarId, WalletFor(_avatarId, ChainType));
        SetupMintSucceeds();
        var manager = BuildManager();

        var result = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_key", ApiKeyId);

        result.IsError.Should().BeFalse();
        result.Result!.WalletProvisioned.Should().BeTrue();
        _walletManager.Verify(m => m.GenerateWalletAsync(
            It.IsAny<WalletGenerateRequest>(), _avatarId, It.IsAny<AZOARequest?>()), Times.Once);
    }

    [Fact]
    public async Task AllocateAsync_ExistingWallet_ReusedNotDuplicated()
    {
        ApproveKyc(_avatarId);
        HasWallet(_avatarId, WalletFor(_avatarId, ChainType));
        SetupMintSucceeds();
        var manager = BuildManager();

        var result = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_key", ApiKeyId);

        result.IsError.Should().BeFalse();
        result.Result!.WalletProvisioned.Should().BeFalse();
        _walletManager.Verify(m => m.GenerateWalletAsync(
            It.IsAny<WalletGenerateRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()), Times.Never);
    }

    // ── IDOR ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AllocateAsync_TransferAlwaysTargetsContractAvatar_NotBody()
    {
        ApproveKyc(_avatarId);
        var wallet = WalletFor(_avatarId, ChainType);
        HasWallet(_avatarId, wallet);

        NftTransferRequest? captured = null;
        _nft.Setup(n => n.TransferAsync(
                It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
            .Callback<Guid, NftTransferRequest, Guid, AZOARequest?, Guid?>((_, req, _, _, _) => captured = req)
            .ReturnsAsync(new AZOAResult<IBlockchainOperation>
            {
                Result = Mock.Of<IBlockchainOperation>(o => o.Id == Guid.NewGuid())
            });

        var attacker = Guid.NewGuid(); // a hostile "owner" the caller might try to inject
        HasWallet(attacker, WalletFor(attacker, ChainType));
        var request = new AllocationRequest
        {
            Kind = AllocationKind.Transfer,
            ChainType = ChainType,
            Amount = "100",
            AssetRecordId = Guid.NewGuid(),
            Memo = "x"
        };

        var result = await manager_AllocateAttackerBody(request, attacker);

        result.IsError.Should().BeFalse();
        captured.Should().NotBeNull();
        // The transfer target is the authorised contract avatar, never the attacker.
        captured!.TargetAvatarId.Should().Be(_avatarId);
        captured.TargetAvatarId.Should().NotBe(attacker);
    }

    /// <summary>
    /// AllocationRequest carries no owner id by design (IDOR-resistant). This
    /// helper documents that even when a caller is the attacker avatar, the
    /// allocation still targets the contract <c>avatarId</c> route value.
    /// </summary>
    private Task<AZOAResult<AllocationResult>> manager_AllocateAttackerBody(
        AllocationRequest request, Guid attackerCaller)
    {
        var manager = BuildManager();
        return manager.AllocateAsync(_avatarId, request, attackerCaller, "pi_transfer", ApiKeyId);
    }

    // ── In-memory idempotency store (faithful claim-once + replay) ────────────

    private sealed class InMemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly Dictionary<string, IdempotencyRecord> _records = new(StringComparer.Ordinal);

        public Task<IdempotencyClaim> TryClaimAsync(string key, string operationType, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var existing))
                return Task.FromResult(new IdempotencyClaim(false, Clone(existing)));

            var record = new IdempotencyRecord
            {
                Key = key,
                OperationType = operationType,
                State = IdempotencyState.InProgress,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _records[key] = record;
            return Task.FromResult(new IdempotencyClaim(true, Clone(record)));
        }

        public Task CompleteAsync(string key, string resultPayload, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var record) && record.State == IdempotencyState.InProgress)
            {
                record.State = IdempotencyState.Completed;
                record.ResultPayload = resultPayload;
                record.UpdatedAt = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task FailAsync(string key, string error, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var record) && record.State == IdempotencyState.InProgress)
            {
                record.State = IdempotencyState.Failed;
                record.Error = error;
                record.UpdatedAt = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
            => Task.FromResult(_records.TryGetValue(key, out var r) ? Clone(r) : null);

        private static IdempotencyRecord Clone(IdempotencyRecord r) => new()
        {
            Key = r.Key,
            OperationType = r.OperationType,
            State = r.State,
            ResultPayload = r.ResultPayload,
            Error = r.Error,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt
        };
    }

    /// <summary>
    /// Models a crash BETWEEN broadcast and settle: <see cref="CompleteAsync"/> is a
    /// no-op so a won claim stays InProgress forever (the orphaned-claim state). A
    /// duplicate must replay "in progress" and NOT re-broadcast; reconciliation later
    /// settles it from the op row's persisted IdempotencyKey (H1).
    /// </summary>
    private sealed class CrashAfterBroadcastIdempotencyStore : IIdempotencyStore
    {
        private readonly Dictionary<string, IdempotencyRecord> _records = new(StringComparer.Ordinal);

        public Task<IdempotencyClaim> TryClaimAsync(string key, string operationType, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var existing))
                return Task.FromResult(new IdempotencyClaim(false, Clone(existing)));

            var record = new IdempotencyRecord
            {
                Key = key,
                OperationType = operationType,
                State = IdempotencyState.InProgress,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _records[key] = record;
            return Task.FromResult(new IdempotencyClaim(true, Clone(record)));
        }

        // The "crash": the process dies before the settle write lands.
        public Task CompleteAsync(string key, string resultPayload, CancellationToken ct) => Task.CompletedTask;

        // A real failure path still settles (so amount/wallet rejections are terminal);
        // only the post-broadcast Complete is lost in this model.
        public Task FailAsync(string key, string error, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var record) && record.State == IdempotencyState.InProgress)
            {
                record.State = IdempotencyState.Failed;
                record.Error = error;
                record.UpdatedAt = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
            => Task.FromResult(_records.TryGetValue(key, out var r) ? Clone(r) : null);

        private static IdempotencyRecord Clone(IdempotencyRecord r) => new()
        {
            Key = r.Key,
            OperationType = r.OperationType,
            State = r.State,
            ResultPayload = r.ResultPayload,
            Error = r.Error,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt
        };
    }
}
