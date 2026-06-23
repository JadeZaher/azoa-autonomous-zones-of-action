using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests;

public class BlockchainOperationManagerTests
{
    private readonly Mock<IBlockchainOperationStore> _store;
    private readonly Mock<IBlockchainProvider> _algoProvider;
    private readonly Mock<IBlockchainProvider> _solProvider;
    private readonly FakeIdempotencyStore _idempotency;
    private readonly BlockchainOperationManager _manager;

    public BlockchainOperationManagerTests()
    {
        _store = new Mock<IBlockchainOperationStore>();

        _algoProvider = new Mock<IBlockchainProvider>();
        _algoProvider.Setup(p => p.ChainType).Returns("Algorand");
        _algoProvider.Setup(p => p.MintAsync(It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new AZOAResult<string> { Result = "algo_tx_123" });

        _solProvider = new Mock<IBlockchainProvider>();
        _solProvider.Setup(p => p.ChainType).Returns("Solana");
        _solProvider.Setup(p => p.MintAsync(It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AZOAResult<string> { Result = "sol_tx_456" });

        var config = new ConfigurationBuilder().Build();
        var factory = new BlockchainProviderFactory(new[] { _algoProvider.Object, _solProvider.Object }, config);
        _idempotency = new FakeIdempotencyStore();
        _manager = new BlockchainOperationManager(_store.Object, factory, _idempotency);
    }

    [Fact]
    public async Task ExecuteAsync_Mint_ShouldDelegateToChainProvider()
    {
        var operation = new BlockchainOperation
        {
            OperationType = "Mint",
            TokenUri = "ipfs://test",
            Amount = 1,
            AssetType = "NFT",
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Algorand" }
        };

        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation op, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = op });

        var result = await _manager.ExecuteAsync(operation);

        result.IsError.Should().BeFalse();
        operation.Status.Should().Be(OperationStatus.Minted);
        operation.Parameters.Should().ContainKey("TxHash");
        _algoProvider.Verify(p => p.MintAsync("ipfs://test", 1UL, "NFT", It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithChainFailure_ShouldSetFailedStatus()
    {
        _algoProvider.Setup(p => p.MintAsync(It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new AZOAResult<string> { IsError = true, Message = "Insufficient funds" });

        var operation = new BlockchainOperation
        {
            OperationType = "Mint",
            Amount = 1,
            Parameters = new Dictionary<string, string> { ["ChainType"] = "Algorand" }
        };

        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation op, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = op });

        var result = await _manager.ExecuteAsync(operation);

        result.IsError.Should().BeFalse();
        operation.Status.Should().Be(OperationStatus.Failed);
        operation.Parameters.Should().ContainKey("Error");
    }

    [Fact]
    public async Task BuildAndExecuteAsync_ShouldUseBuilderPattern()
    {
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation op, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = op });

        var result = await _manager.BuildAndExecuteAsync(builder =>
            builder.ForAvatar(Guid.NewGuid())
                   .Mint("ipfs://test", 1, "NFT")
                   .Build());

        result.IsError.Should().BeFalse();
        result.Result!.OperationType.Should().Be("Mint");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnOperation()
    {
        var operation = new BlockchainOperation { Id = Guid.NewGuid(), Status = OperationStatus.Pending };
        _store.Setup(p => p.GetByIdAsync(operation.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = operation });

        var result = await _manager.GetAsync(operation.Id);

        result.Result.Should().NotBeNull();
        result.Result!.Status.Should().Be(OperationStatus.Pending);
    }

    [Fact]
    public async Task GetByAvatarAsync_ShouldFilterByAvatarId()
    {
        var avatarId = Guid.NewGuid();
        _store.Setup(p => p.GetByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IBlockchainOperation>>
                 {
                     Result = new[] { new BlockchainOperation { AvatarId = avatarId } }
                 });

        var result = await _manager.GetByAvatarAsync(avatarId);

        result.Result.Should().HaveCount(1);
    }

    // The manager must run the chain effect exactly once per logical operation
    // even under duplicate/concurrent ExecuteAsync, and a value-bearing param
    // change must derive a distinct key. Single-winner under parallelism comes
    // from the INSERT-WINS FakeIdempotencyStore.

    private BlockchainOperation NewMintOp(string wallet = "WALLET_A", string tokenUri = "ipfs://nft", ulong amount = 1, string asset = "NFT")
        => new()
        {
            OperationType = "Mint",
            TokenUri = tokenUri,
            // The typed ulong Amount field is the single source of truth the real
            // DeriveIdempotencyKey reads — set it (NOT a Parameters["Amount"] string)
            // so the test exercises the production key-derivation path.
            Amount = amount,
            AssetType = asset,
            Parameters = new Dictionary<string, string>
            {
                ["ChainType"] = "Algorand",
                ["WalletAddress"] = wallet
            }
        };

    [Fact]
    public async Task ExecuteAsync_DuplicateOperation_ExecutesChainEffectExactlyOnce()
    {
        // Count real chain invocations.
        var mintCalls = 0;
        _algoProvider.Setup(p => p.MintAsync(It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(() => { Interlocked.Increment(ref mintCalls); return new AZOAResult<string> { Result = "algo_tx_DUP" }; });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation op, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = op });

        // Two SEPARATE operation instances with IDENTICAL logical inputs
        // (same chain/opType/wallet/params) — different fresh op.Id GUIDs,
        // exactly the real-world "client retried the request" scenario.
        var first = NewMintOp();
        var second = NewMintOp();
        first.Id.Should().NotBe(second.Id, "the retry must carry a fresh GUID — dedupe must NOT rely on op.Id");

        var r1 = await _manager.ExecuteAsync(first);
        var r2 = await _manager.ExecuteAsync(second);

        r1.IsError.Should().BeFalse();
        r2.IsError.Should().BeFalse();

        // Chain effect ran ONCE for the original; the duplicate did NOT
        // re-broadcast.
        mintCalls.Should().Be(1, "the duplicate request must not re-execute the irreversible chain effect");

        // The duplicate replays the ORIGINAL outcome incl. the TxHash.
        first.Status.Should().Be(OperationStatus.Minted);
        first.Parameters["TxHash"].Should().Be("algo_tx_DUP");
        second.Parameters.Should().ContainKey("TxHash");
        second.Parameters["TxHash"].Should().Be("algo_tx_DUP");
        r2.Message.Should().Contain("Duplicate request");

        // Exactly one idempotency record exists for the logical op and it is
        // terminal-Completed — no spurious second "broadcast" row.
        _idempotency.RecordCount.Should().Be(1);
        var keys = _idempotency.Keys.ToList();
        keys.Should().HaveCount(1);
        (await _idempotency.GetAsync(keys[0], CancellationToken.None))!.State.Should().Be(IdempotencyState.Completed);

        // SaveBlockchainOperationAsync is NOT invoked for the deduped call
        // (no second op row written): original wins → 2 saves (initial +
        // settle); duplicate → 0 saves.
        _store.Verify(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentDuplicateOperations_ExecuteChainEffectExactlyOnce()
    {
        const int concurrency = 16;
        var mintCalls = 0;
        _algoProvider.Setup(p => p.MintAsync(It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(() => { Interlocked.Increment(ref mintCalls); return new AZOAResult<string> { Result = "algo_tx_CONC" }; });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation op, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = op });

        // BlockchainProviderFactory memoizes resolved providers in a plain
        // (non-concurrent) Dictionary; concurrent first-touch writes are an
        // incidental harness data-race orthogonal to the claim-race under test.
        // Prime the memo single-threaded first via a throwaway op with a
        // DISTINCT wallet (⇒ different idempotency key) so the concurrent phase
        // only ever takes the lock-free read path.
        var warm = await _manager.ExecuteAsync(NewMintOp(wallet: "WARMUP_WALLET"));
        warm.IsError.Should().BeFalse();
        Interlocked.Exchange(ref mintCalls, 0); // discard the warm-up broadcast

        // Snapshot pre-race keys so we can isolate the key created BY the race.
        var preRaceKeys = _idempotency.Keys.ToHashSet();

        var ops = Enumerable.Range(0, concurrency).Select(_ => NewMintOp()).ToArray();

        // ready: every task signals once parked at the start line. go: a single
        // Set() releases all N at once so the idempotency claims genuinely race.
        using var ready = new CountdownEvent(concurrency);
        using var go = new ManualResetEventSlim(false);

        var tasks = ops.Select(op => Task.Run(async () =>
        {
            ready.Signal();      // I am at the start line.
            go.Wait();           // Hold until the gun fires.
            return await _manager.ExecuteAsync(op);
        })).ToArray();

        ready.Wait();            // All N tasks parked at the gate.
        go.Set();                // Fire — all N race simultaneously.

        var results = await Task.WhenAll(tasks);

        // THE invariant: exactly one on-chain broadcast despite N concurrent
        // identical ops (the manager honored the single claim winner). This is
        // the strict, deterministic proof of "exactly-once under concurrency".
        mintCalls.Should().Be(1, "the manager must honor the single claim winner — no double broadcast under concurrency");

        // No caller errored. (A loser whose claim landed while the winner was
        // still mid-flight legitimately replays a non-terminal "in progress"
        // result with NO TxHash — that is CORRECT no-double-broadcast
        // behavior, not a failure. So we do NOT require every caller to see
        // the txid; we require correctness:)
        results.Should().OnlyContain(r => r.IsError == false);
        results.Should().OnlyContain(r => r.Result != null);

        // Whatever a caller observed, it is consistent: anyone who got a
        // TxHash got THE one true txid (never a different/second broadcast).
        // Losers that observed an in-progress claim correctly carry NO TxHash
        // — that is explicitly allowed and must not fail the test.
        foreach (var r in results)
        {
            if (r.Result!.Parameters.TryGetValue("TxHash", out var tx))
                tx.Should().Be("algo_tx_CONC", "no caller may ever observe a second/different broadcast");
        }

        // At least the winner observed the terminal success with the txid.
        results.Any(r => r.Result!.Parameters.TryGetValue("TxHash", out var t) && t == "algo_tx_CONC")
            .Should().BeTrue("the claim winner must surface the single successful on-chain result");

        // The N raced ops share IDENTICAL logical inputs ⇒ they collapse to
        // EXACTLY ONE new idempotency key (beyond the pre-race warm-up key),
        // and it settles terminal-Completed carrying the single true txid.
        var racedKeys = _idempotency.Keys.Where(k => !preRaceKeys.Contains(k)).ToList();
        racedKeys.Should().HaveCount(1, "N identical concurrent ops must collapse to a single logical idempotency key");
        var rec = await _idempotency.GetAsync(racedKeys[0], CancellationToken.None);
        rec!.State.Should().Be(IdempotencyState.Completed);
        rec.ResultPayload.Should().Be("algo_tx_CONC");
    }

    [Fact]
    public async Task ExecuteAsync_DifferentValueBearingParam_IsNotDeduped()
    {
        var mintCalls = 0;
        _algoProvider.Setup(p => p.MintAsync(It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(() => { Interlocked.Increment(ref mintCalls); return new AZOAResult<string> { Result = "algo_tx_X" }; });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation op, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = op });

        // Two ops that differ ONLY in a value-bearing param (Amount). They
        // are DIFFERENT logical operations and must BOTH execute — an
        // over-broad key would wrongly suppress the second legitimate op.
        var opA = NewMintOp(amount: 1);
        var opB = NewMintOp(amount: 2);

        var rA = await _manager.ExecuteAsync(opA);
        var rB = await _manager.ExecuteAsync(opB);

        rA.IsError.Should().BeFalse();
        rB.IsError.Should().BeFalse();

        // Both executed — no false dedupe of a distinct op.
        mintCalls.Should().Be(2, "ops differing in a value-bearing param must derive distinct keys and both execute");
        opA.Status.Should().Be(OperationStatus.Minted);
        opB.Status.Should().Be(OperationStatus.Minted);

        // Distinct keys ⇒ two records.
        _idempotency.RecordCount.Should().Be(2);
        _idempotency.Keys.Distinct().Should().HaveCount(2);
    }

    // ── C-1 regression: large mint amounts must NOT collide on the idempotency key ──
    //
    // Before the fix the typed mint Amount was an int and AllocationManager clamped
    // the real ulong with ClampToInt (ulong→int saturating to int.MaxValue). The
    // idempotency key is derived from that typed Amount, so TWO distinct allocations
    // whose amounts both exceed int.MaxValue clamped to the SAME 2147483647, derived
    // the SAME key, and the second silently deduped → it NEVER broadcast → silent
    // under-allocation. Widening Amount to ulong keys off the true value.
    //
    // This drives the REAL DeriveIdempotencyKey path (no mocked ExecuteAsync): two
    // distinct amounts both above int.MaxValue, same wallet/asset/avatar. Pre-fix:
    // identical keys ⇒ ONE record, mintCalls == 1 (the bug). Post-fix: distinct keys
    // ⇒ TWO records, mintCalls == 2.
    [Fact]
    public async Task ExecuteAsync_DistinctLargeMintAmounts_AboveIntMaxValue_DeriveDistinctKeys()
    {
        var mintCalls = 0;
        _algoProvider.Setup(p => p.MintAsync(It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(() => { Interlocked.Increment(ref mintCalls); return new AZOAResult<string> { Result = "algo_tx_BIG" }; });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation op, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = op });

        // Both amounts exceed int.MaxValue (2_147_483_647). Under the old int-clamp
        // both collapsed to int.MaxValue and collided; they MUST now stay distinct.
        const ulong amountA = 5_000_000_000UL;
        const ulong amountB = 9_000_000_000UL;
        amountA.Should().BeGreaterThan(int.MaxValue);
        amountB.Should().BeGreaterThan(int.MaxValue);

        // Root-cause pin: under the historic int-clamp these DISTINCT amounts both
        // saturated to int.MaxValue, so the key (derived from the clamped Amount)
        // collided and the second op was silently deduped. This documents WHY the
        // pre-fix code returned mintCalls == 1; the assertions below prove the fix.
        HistoricIntClamp(amountA).Should().Be(HistoricIntClamp(amountB),
            "the pre-fix int-clamp collapsed both distinct amounts to int.MaxValue — the source of C-1");

        var opA = NewMintOp(amount: amountA);
        var opB = NewMintOp(amount: amountB);

        var rA = await _manager.ExecuteAsync(opA);
        var rB = await _manager.ExecuteAsync(opB);

        rA.IsError.Should().BeFalse();
        rB.IsError.Should().BeFalse();

        // The provider received the FULL ulong amounts (no clamp/truncation).
        _algoProvider.Verify(p => p.MintAsync(It.IsAny<string>(), amountA, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()), Times.Once);
        _algoProvider.Verify(p => p.MintAsync(It.IsAny<string>(), amountB, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()), Times.Once);

        // THE C-1 invariant: two distinct large allocations derive DISTINCT
        // idempotency keys and BOTH broadcast. The pre-fix bug would fail here with
        // mintCalls == 1 and a single key (the second silently deduped).
        mintCalls.Should().Be(2, "distinct amounts above int.MaxValue must derive distinct keys and both broadcast — no clamp collision");
        _idempotency.RecordCount.Should().Be(2);
        _idempotency.Keys.Distinct().Should().HaveCount(2, "the idempotency key must be derived from the TRUE ulong amount, not a clamped int");
    }

    /// <summary>
    /// Models the historic (pre-C-1-fix) saturating ulong→int clamp purely to
    /// document the collision. Test-only; the production path now keys off the
    /// true ulong so this projection no longer exists outside this regression.
    /// </summary>
    private static int HistoricIntClamp(ulong amount)
        => amount > int.MaxValue ? int.MaxValue : (int)amount;

    // ── value-path-wiring M1: confirm-timeout preserves txId (Pending, not Failed) ──

    [Fact]
    public async Task ExecuteAsync_ConfirmTimeout_RecordsPendingConfirmationWithTxHash_NotFailed()
    {
        const string txId = "algo_tx_pending_M1";
        var mintCalls = 0;
        // The provider broadcast succeeded but did not confirm in the poll window:
        // it returns a SUCCESS carrying the txId + the pending-confirmation marker.
        _algoProvider.Setup(p => p.MintAsync(
                It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref mintCalls);
                return new AZOAResult<string>
                {
                    IsError = false,
                    Result = txId,
                    Message = $"{OperationStatus.PendingConfirmationMarker}: submitted but not yet confirmed."
                };
            });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation op, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = op });

        var op = NewMintOp();
        var result = await _manager.ExecuteAsync(op);

        result.IsError.Should().BeFalse();
        // The op is PendingConfirmation (NOT Failed, NOT a terminal success) and it
        // CARRIES the tx hash so reconciliation can settle it from chain truth.
        op.Status.Should().Be(OperationStatus.PendingConfirmation);
        op.Status.Should().NotBe(OperationStatus.Failed, "a slow-but-valid tx must never be false-Failed");
        op.Parameters.Should().ContainKey("TxHash");
        op.Parameters["TxHash"].Should().Be(txId);

        // The idempotency record stays InProgress (recoverable) — not Completed,
        // not Failed — until positive chain signal settles it.
        var key = _idempotency.Keys.Single();
        var record = await _idempotency.GetAsync(key, CancellationToken.None);
        record!.State.Should().Be(IdempotencyState.InProgress,
            "a pending-confirmation op leaves the claim recoverable for reconciliation");

        // A duplicate must REPLAY in-progress and must NOT re-broadcast.
        var dup = await _manager.ExecuteAsync(NewMintOp());
        dup.IsError.Should().BeFalse();
        dup.Result!.Status.Should().Be(OperationStatus.Pending,
            "a duplicate against an InProgress claim replays pending — never re-broadcasts");
        mintCalls.Should().Be(1, "the pending-confirmation op must not be re-broadcast by a duplicate");
    }

    [Fact]
    public async Task ExecuteAsync_AwaitingSignatureOp_IsNotMarkedCompleted()
    {
        // Provider signals client-side signing required (the manager maps a
        // "Requires client-side signing" message → AwaitingSignature). Such
        // an op was NOT broadcast server-side, so it must NOT be a terminal
        // Completed idempotency record (no false "done").
        _algoProvider.Setup(p => p.MintAsync(It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new AZOAResult<string>
                     {
                         IsError = false,
                         Result = "op_ref_unsigned",
                         Message = "Requires client-side signing. Sign and submit the returned transaction."
                     });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IBlockchainOperation op, CancellationToken _) => new AZOAResult<IBlockchainOperation> { Result = op });

        var op = NewMintOp();
        var result = await _manager.ExecuteAsync(op);

        result.IsError.Should().BeFalse();
        op.Status.Should().Be(OperationStatus.AwaitingSignature);

        // The idempotency record stays NON-terminal (InProgress) — never
        // Completed/Failed — because nothing irreversible was broadcast.
        var key = _idempotency.Keys.Single();
        var record = await _idempotency.GetAsync(key, CancellationToken.None);
        record.Should().NotBeNull();
        record!.State.Should().Be(IdempotencyState.InProgress,
            "an AwaitingSignature op was not broadcast server-side — it must NOT be a terminal Completed record");
        record.ResultPayload.Should().BeNullOrEmpty("no TxHash exists — nothing was submitted on-chain");
    }
}
