using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Connection;
using Azoa.SurrealDb.Client.Query;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Bridge;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Reconciliation;

namespace AZOA.WebAPI.IntegrationTests.Gates;

/// <summary>
/// Gate G7 — Chain reconciliation re-derives truth from chain confirmations
/// after a mid-op crash.
///
/// <para>
/// Spec guardrail: after a crash that leaves a bridge row in the
/// <see cref="BridgeStatus.Redeeming"/> state (the redemption side-effect was
/// never persisted), a single reconciliation pass driven by a provider stub that
/// returns known-confirmed truth MUST transition the row to
/// <see cref="BridgeStatus.Completed"/>, settle the orphaned
/// <see cref="IdempotencyState.InProgress"/> idempotency record, and be
/// idempotent on re-run — all against the real SurrealDB stores.
/// </para>
/// </summary>
[Trait("Category", "Gate")]
public class G7_ReconciliationDrillTest : IntegrationTestBase
{
    public G7_ReconciliationDrillTest(AZOATestWebApplicationFactory factory)
        : base(factory)
    {
    }

    [SkippableFact]
    public async Task G7_KillMidRedeem_Reconciliation_ConvergesToChainTruth()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        // ── Store wiring against the isolated test namespace ─────────────────
        var executor        = BuildExecutor();
        var bridgeStore     = new SurrealBridgeStore(executor);
        var idempotencyStore = new SurrealIdempotencyStore(executor);

        // ── Seed an orphaned InProgress idempotency claim ────────────────────
        // Models a crash that left the claim open after the on-chain effect landed.
        const string idemKey = "bridge-redeem:g7_kill:digest_g7_abc";
        var claim = await idempotencyStore.TryClaimAsync(idemKey, "bridge-redeem", CancellationToken.None);
        claim.Won.Should().BeTrue("setup: claim must succeed on a fresh key");

        // ── Seed the bridge row at the kill-point state ───────────────────────
        // Status=Redeeming, LockTxHash set, MintTxHash (redemption) set.
        // The Redeeming→Completed write never persisted — the simulated crash.
        var bridgeId = $"br_g7_{Guid.NewGuid():N}";
        var bridge = new BridgeTransactionResult
        {
            Id             = bridgeId,
            AvatarId       = Guid.NewGuid(),
            SourceChain    = "Solana",
            TargetChain    = "Algorand",
            SourceTokenId  = "SOL:native",
            SourceAddress  = $"src_{bridgeId}",
            TargetAddress  = $"tgt_{bridgeId}",
            Amount         = 500,
            Mode           = BridgeMode.Wormhole,
            Status         = BridgeStatus.Redeeming,
            LockTxHash     = "lock_tx_g7_landed",
            // The redemption tx landed on Algorand but the completion write crashed.
            MintTxHash     = "redeem_tx_g7_confirmed",
            IdempotencyKey = idemKey,
            // Older than BridgeStaleAfterSeconds=60 — eligible for reconciliation.
            CreatedAt      = DateTime.UtcNow.AddMinutes(-30),
        };
        await bridgeStore.AddBridgeAsync(bridge);

        // ── Pre-kill assertions ───────────────────────────────────────────────
        var preKill = await bridgeStore.GetBridgeAsync(bridgeId);
        preKill.Should().NotBeNull();
        preKill!.Status.Should().Be(BridgeStatus.Redeeming,
            "pre-condition: row must be stuck at Redeeming (kill-point state)");

        var preKillIdem = await idempotencyStore.GetAsync(idemKey, CancellationToken.None);
        preKillIdem.Should().NotBeNull();
        preKillIdem!.State.Should().Be(IdempotencyState.InProgress,
            "pre-condition: orphaned claim must still be InProgress");

        // ── Reconciliation restart with the truth-provider stub ───────────────
        // The truth provider returns confirmed=true for the redemption tx hash,
        // modelling the on-chain truth observed after crash recovery.
        var truthProvider = new ConfirmingChainProviderStub("redeem_tx_g7_confirmed");
        var factory       = new SingleProviderFactory(truthProvider);
        var svc           = BuildReconciliationService(bridgeStore, idempotencyStore, factory);

        // ── Act: first reconciliation pass ────────────────────────────────────
        var report = await svc.ReconcileBridgeAsync(CancellationToken.None);

        // ── Assert: bridge row converged Redeeming → Completed ───────────────
        report.Scanned.Should().Be(1,
            "exactly one stale non-terminal row is in the store");
        report.Advanced.Should().Be(1,
            "chain-confirmed redemption must advance Redeeming→Completed");
        report.Failed.Should().Be(0);
        report.Errors.Should().Be(0);

        var postReconcile = await bridgeStore.GetBridgeAsync(bridgeId);
        postReconcile.Should().NotBeNull();
        postReconcile!.Status.Should().Be(BridgeStatus.Completed,
            "G7: bridge row must be Completed after reconciliation derives chain truth");
        postReconcile.CompletedAt.Should().NotBeNull(
            "CompletedAt is stamped when the row transitions to Completed");

        // ── Assert: orphaned idempotency record is settled ────────────────────
        var settledIdem = await idempotencyStore.GetAsync(idemKey, CancellationToken.None);
        settledIdem.Should().NotBeNull();
        settledIdem!.State.Should().Be(IdempotencyState.Completed,
            "G7: orphaned InProgress idempotency record must be settled to Completed");
        settledIdem.ResultPayload.Should().Be("redeem_tx_g7_confirmed",
            "the confirmed redeem tx hash is cached as the idempotency result payload");

        // ── Assert: truth provider was consulted ──────────────────────────────
        truthProvider.GetTransactionStatusCallCount.Should().BeGreaterThan(0,
            "reconciliation must consult the truth provider for the redemption tx hash");

        // ── Assert: idempotent re-run ─────────────────────────────────────────
        // The row is now terminal (Completed). GetNonTerminalBridgeIds must not
        // return it; the provider must not be called again; the idempotency
        // record must remain Completed unchanged.
        var completedAtBeforeRerun = postReconcile.CompletedAt;
        var callsBeforeRerun       = truthProvider.GetTransactionStatusCallCount;

        var secondReport = await svc.ReconcileBridgeAsync(CancellationToken.None);

        secondReport.Scanned.Should().Be(0,
            "G7: a terminal row is not a reconciliation candidate — idempotent re-run scans nothing");
        secondReport.Advanced.Should().Be(0,
            "G7: second pass must not advance the already-Completed row");
        secondReport.Errors.Should().Be(0);

        var afterRerun = await bridgeStore.GetBridgeAsync(bridgeId);
        afterRerun!.Status.Should().Be(BridgeStatus.Completed,
            "G7: status must remain Completed after the second pass");

        afterRerun.CompletedAt.Should().BeCloseTo(
            completedAtBeforeRerun!.Value, TimeSpan.FromSeconds(1),
            "G7: CompletedAt must not change on an idempotent re-run — row not re-written");

        truthProvider.GetTransactionStatusCallCount.Should().Be(
            callsBeforeRerun,
            "G7: truth provider must NOT be consulted during the idempotent re-run " +
            "(terminal row is excluded from candidates before any chain probe)");

        var idemAfterRerun = await idempotencyStore.GetAsync(idemKey, CancellationToken.None);
        idemAfterRerun!.State.Should().Be(IdempotencyState.Completed,
            "G7: idempotency record remains Completed on second pass — never re-duplicated");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// Build a SurrealDB executor bound to this test instance's isolated namespace.
    private ISurrealExecutor BuildExecutor()
    {
        var options = new SurrealConnectionOptions
        {
            Endpoint  = SurrealTestDefaults.Endpoint,
            Namespace = TestNamespace,
            Database  = "test",
            User      = SurrealTestDefaults.User,
            Password  = SurrealTestDefaults.Password,
        };

        var http       = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var connection = new HttpSurrealConnection(http, options);
        return new DefaultSurrealExecutor(connection);
    }

    private static ReconciliationService BuildReconciliationService(
        SurrealBridgeStore      bridgeStore,
        SurrealIdempotencyStore idempotencyStore,
        IBlockchainProviderFactory factory)
    {
        var options = new ReconciliationOptions
        {
            Enabled                        = true,
            BatchSize                      = 100,
            BridgeStaleAfterSeconds        = 60,
            BridgeHardStuckAfterSeconds    = 900,
            OperationStaleAfterSeconds     = 60,
            OperationHardStuckAfterSeconds = 900,
        };

        return new ReconciliationService(
            bridgeStore,
            factory,
            idempotencyStore,
            NullLogger<ReconciliationService>.Instance,
            Options.Create(options));
    }

    // ── Nested fakes (no Moq — integration tests project does not reference it)─

    /// <summary>
    /// A minimal <see cref="IBlockchainProviderFactory"/> that returns a single
    /// provider for every <c>GetProvider</c> / <c>GetDefaultProvider</c> call.
    /// </summary>
    private sealed class SingleProviderFactory : IBlockchainProviderFactory
    {
        private readonly IBlockchainProvider _provider;

        public SingleProviderFactory(IBlockchainProvider provider)
        {
            _provider = provider;
        }

        public IBlockchainProvider GetProvider(string chainType, ChainNetwork network)
            => _provider;

        public IBlockchainProvider GetDefaultProvider()
            => _provider;

        public IEnumerable<IBlockchainProvider> GetAllEnabledProviders()
            => new[] { _provider };

        public bool TryGetModule<T>(IBlockchainProvider provider, out T? module)
            where T : class, IBlockchainProviderModule
        {
            module = null;
            return false;
        }
    }

    /// <summary>
    /// A chain provider stub that returns chain-confirmed truth (Algorand
    /// <c>confirmed=true</c>) for the single expected tx hash, and an
    /// indeterminate error result for any other hash. Exposes a call counter so
    /// the test can assert the truth provider is (or is not) consulted.
    ///
    /// All on-chain mutating methods throw so a test failure surfaces immediately
    /// if reconciliation unexpectedly calls a write method.
    /// </summary>
    private sealed class ConfirmingChainProviderStub : IBlockchainProvider
    {
        private readonly string _confirmedTxHash;
        private int _callCount;

        public ConfirmingChainProviderStub(string confirmedTxHash)
        {
            _confirmedTxHash = confirmedTxHash;
        }

        public int GetTransactionStatusCallCount => Volatile.Read(ref _callCount);

        // ── IBlockchainProvider — identity ────────────────────────────────────
        public string ChainType       => "Algorand";
        public ChainNetwork ActiveNetwork => ChainNetwork.Devnet;
        public bool SupportsBridging  => false;
        public bool SupportsFaucet    => false;

        public void Initialize(BlockchainNetworkConfig config, ChainNetwork network) { }

        public bool TryGetModule<T>(out T? module) where T : class, IBlockchainProviderModule
        {
            module = null;
            return false;
        }

        // ── The ONE method reconciliation legitimately calls ──────────────────
        public Task<AZOAResult<Dictionary<string, object>>> GetTransactionStatusAsync(
            string txHash, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);

            if (txHash == _confirmedTxHash)
            {
                return Task.FromResult(new AZOAResult<Dictionary<string, object>>
                {
                    IsError = false,
                    Result  = new Dictionary<string, object>
                    {
                        ["confirmed"]      = true,
                        ["confirmedRound"] = 12L,
                    },
                });
            }

            return Task.FromResult(new AZOAResult<Dictionary<string, object>>
            {
                IsError = true,
                Message = "tx not found",
                Result  = null,
            });
        }

        // Mirrors the base provider default: derive the confirmation verdict from
        // the status probe via the shared classifier. Counts as a status call.
        public async Task<AZOAResult<AZOA.WebAPI.Models.Blockchain.ChainConfirmation>>
            GetTransactionConfirmationAsync(string txHash, CancellationToken ct = default)
        {
            var status = await GetTransactionStatusAsync(txHash, ct);
            return new AZOAResult<AZOA.WebAPI.Models.Blockchain.ChainConfirmation>
            {
                Result = AZOA.WebAPI.Core.Blockchain.ChainTxClassifier.Classify(status)
            };
        }

        // ── Mutating methods — must never be called by reconciliation ─────────

        public Task<AZOAResult<string>> GetBalanceAsync(
            string address, string? tokenId = null, CancellationToken ct = default) =>
            MutationNotExpected<string>(nameof(GetBalanceAsync));

        public Task<AZOAResult<bool>> ValidateAddressAsync(
            string address, CancellationToken ct = default) =>
            MutationNotExpected<bool>(nameof(ValidateAddressAsync));

        public Task<AZOAResult<string>> MintAsync(
            string tokenUri, ulong amount, string assetType,
            string walletAddress, SigningContext? signingContext = null,
            CancellationToken ct = default) =>
            MutationNotExpected<string>(nameof(MintAsync));

        public Task<AZOAResult<string>> MintWrappedAsync(
            string sourceChain, string sourceTokenId, string tokenUri,
            int amount, string recipientAddress, CancellationToken ct = default) =>
            MutationNotExpected<string>(nameof(MintWrappedAsync));

        public Task<AZOAResult<string>> BurnAsync(
            string tokenId, ulong amount, string walletAddress,
            SigningContext? signingContext = null, CancellationToken ct = default) =>
            MutationNotExpected<string>(nameof(BurnAsync));

        public Task<AZOAResult<string>> BurnWrappedAsync(
            string tokenId, int amount, string sourceChain,
            string sourceRecipient, string walletAddress, CancellationToken ct = default) =>
            MutationNotExpected<string>(nameof(BurnWrappedAsync));

        public Task<AZOAResult<string>> TransferAsync(
            string tokenId, string fromAddress, string toAddress,
            ulong amount, SigningContext? signingContext = null,
            CancellationToken ct = default) =>
            MutationNotExpected<string>(nameof(TransferAsync));

        public Task<AZOAResult<string>> ExchangeAsync(
            string sourceTokenId, string targetTokenId,
            string exchangeRate, string walletAddress, CancellationToken ct = default) =>
            MutationNotExpected<string>(nameof(ExchangeAsync));

        public Task<AZOAResult<string>> SwapAsync(
            string tokenIn, string tokenOut, decimal amountIn,
            decimal minAmountOut, string walletAddress, CancellationToken ct = default) =>
            MutationNotExpected<string>(nameof(SwapAsync));

        public Task<AZOAResult<Dictionary<string, object>>> GetTokenMetadataAsync(
            string tokenId, CancellationToken ct = default) =>
            MutationNotExpected<Dictionary<string, object>>(nameof(GetTokenMetadataAsync));

        public Task<AZOAResult<List<Dictionary<string, object>>>> GetTokensByOwnerAsync(
            string ownerAddress, CancellationToken ct = default) =>
            MutationNotExpected<List<Dictionary<string, object>>>(nameof(GetTokensByOwnerAsync));

        public Task<AZOAResult<string>> DeployContractAsync(
            byte[] contractCode, string walletAddress,
            Dictionary<string, object>? args = null, CancellationToken ct = default) =>
            MutationNotExpected<string>(nameof(DeployContractAsync));

        public Task<AZOAResult<object>> CallContractAsync(
            string contractAddress, string method,
            Dictionary<string, object> args, string walletAddress,
            CancellationToken ct = default) =>
            MutationNotExpected<object>(nameof(CallContractAsync));

        public Task<AZOAResult<Dictionary<string, object>>> GetChainInfoAsync(
            CancellationToken ct = default) =>
            MutationNotExpected<Dictionary<string, object>>(nameof(GetChainInfoAsync));

        public Task<AZOAResult<string>> LockForBridgeAsync(
            string tokenId, string vaultAddress, int amount,
            string targetChain, string targetRecipient, CancellationToken ct = default) =>
            MutationNotExpected<string>(nameof(LockForBridgeAsync));

        public Task<AZOAResult<bool>> VerifyBridgeProofAsync(
            string proofData, string sourceChain, string targetChainId,
            CancellationToken ct = default) =>
            MutationNotExpected<bool>(nameof(VerifyBridgeProofAsync));

        public Task<AZOAResult<FaucetDispenseResult>> DispenseFromFaucetAsync(
            string toAddress, decimal amount, string? idempotencyKey = null,
            CancellationToken ct = default) =>
            MutationNotExpected<FaucetDispenseResult>(nameof(DispenseFromFaucetAsync));

        private static Task<AZOAResult<T>> MutationNotExpected<T>(string method) =>
            throw new InvalidOperationException(
                $"G7: ConfirmingChainProviderStub.{method} must never be called by " +
                "reconciliation — the service OBSERVES chain truth, it NEVER performs " +
                "on-chain mutations.");
    }
}
