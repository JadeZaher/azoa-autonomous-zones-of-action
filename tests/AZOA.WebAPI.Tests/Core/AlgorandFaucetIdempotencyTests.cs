using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Core;

/// <summary>
/// Idempotency-gate tests for <see cref="AlgorandFaucet"/> — the one real
/// server-side broadcaster in the API. Proves a retried/concurrent topup
/// submits the on-chain payment exactly once per idempotency key with no live
/// node.
///
/// Network isolation: the faucet calls <see cref="IIdempotencyStore.TryClaimAsync"/>
/// BEFORE any account/SDK/submit statement. A loser (Won==false) returns from
/// the dedup switch before the try block, so no node is contacted. The winner
/// proceeds into the try but the chain-less in-memory config + invalid mnemonic
/// make GetNetworkConfig throw OFFLINE before any HTTP — its catch records
/// FailAsync. Net: exactly one submit attempt, deterministic, no Algod. The
/// scoped store is resolved via the injected <see cref="IServiceScopeFactory"/>,
/// so the same shared fake instance is used per call.
/// </summary>
public class AlgorandFaucetIdempotencyTests
{
    private const string FaucetChain = "Algorand";
    private const string FaucetOperationType = "faucet-dispense";

    // Passes IsNullOrWhiteSpace but is NOT a valid 25-word Algorand mnemonic.
    // Combined with the deliberately chain-less config, the claim winner fails
    // OFFLINE inside the try (GetNetworkConfig throws before any HTTP, and the
    // mnemonic would also be rejected) so no node is ever contacted.
    private const string InvalidMnemonic = "not a real algorand mnemonic phrase used only for offline test isolation";

    private static IConfiguration BuildConfig(string? mnemonic = InvalidMnemonic)
    {
        var dict = new Dictionary<string, string?>
        {
            ["Blockchain:DefaultNetwork"] = "devnet"
        };
        if (mnemonic != null)
            dict["Blockchain:Faucet:Algorand:Mnemonic"] = mnemonic;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    /// <summary>Faucet resolves the same fake store per call via the injected
    /// <see cref="IServiceScopeFactory"/> — the production scoped pattern.</summary>
    private static (AlgorandFaucet faucet, FakeIdempotencyStore store) BuildFaucet(
        string? mnemonic = InvalidMnemonic)
    {
        var store = new FakeIdempotencyStore();
        var services = new ServiceCollection();
        services.AddScoped<IIdempotencyStore>(_ => store);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var faucet = new AlgorandFaucet(
            BuildConfig(mnemonic),
            NullLogger<AlgorandFaucet>.Instance,
            scopeFactory);
        return (faucet, store);
    }

    /// <summary>The deterministic content-addressed key the 2-arg overload
    /// derives (mirrors AlgorandFaucet.DispenseAsync(addr, amt)).</summary>
    private static string DerivedKey(string toAddress, decimal amountAlgo)
        => OperationIdGenerator.Generate(
            FaucetChain, FaucetOperationType, toAddress,
            amountAlgo.ToString(CultureInfo.InvariantCulture));

    [Fact]
    public void IsConfigured_ReflectsMnemonicPresence()
    {
        var (configured, _) = BuildFaucet();
        configured.IsConfigured.Should().BeTrue();

        var (unconfigured, _) = BuildFaucet(mnemonic: null);
        unconfigured.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task DispenseAsync_WhenPriorCompletedRecordExists_ReplaysTxidAndDoesNotSubmit()
    {
        const string addr = "RECIPIENT_ADDRESS_AAA";
        const decimal amount = 5m;
        const string priorTxid = "PRIOR_TXID_DEADBEEF";

        var (faucet, store) = BuildFaucet();

        // Pre-seed a terminal Completed record under the key the faucet derives
        // so the fresh claim loses and returns before the submit block.
        var key = DerivedKey(addr, amount);
        store.Seed(key, FaucetOperationType, IdempotencyState.Completed, resultPayload: priorTxid);

        // Must NOT throw: on this offline config the submit path would throw,
        // so a clean return proves it was never entered.
        var txid = await faucet.DispenseAsync(addr, amount);

        txid.Should().Be(priorTxid,
            "a duplicate dispense replays the original txid and performs NO on-chain submit");

        var rec = await store.GetAsync(key, CancellationToken.None);
        rec!.State.Should().Be(IdempotencyState.Completed);
        rec.ResultPayload.Should().Be(priorTxid);
        store.RecordCount.Should().Be(1, "no new claim row — purely a replay");
    }

    [Fact]
    public async Task DispenseAsync_WhenPriorFailedRecordExists_DoesNotResubmitAndSurfacesFailure()
    {
        const string addr = "RECIPIENT_ADDRESS_BBB";
        const decimal amount = 2m;

        var (faucet, store) = BuildFaucet();
        var key = DerivedKey(addr, amount);
        store.Seed(key, FaucetOperationType, IdempotencyState.Failed, error: "prior dispense failed");

        // Won=false + Failed ⇒ throws the dedup-gate InvalidOperationException
        // WITHOUT re-submitting an irreversible broadcast.
        var act = async () => await faucet.DispenseAsync(addr, amount);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*prior faucet dispense*failed*");

        store.RecordCount.Should().Be(1);
        (await store.GetAsync(key, CancellationToken.None))!.State.Should().Be(IdempotencyState.Failed);
    }

    [Fact]
    public async Task DispenseAsync_WhenInProgressRecordExists_DoesNotResubmit()
    {
        const string addr = "RECIPIENT_ADDRESS_CCC";
        const decimal amount = 1m;

        var (faucet, store) = BuildFaucet();
        var key = DerivedKey(addr, amount);
        store.Seed(key, FaucetOperationType, IdempotencyState.InProgress);

        // An in-flight original ⇒ dedup-gate conflict, no re-submit.
        var act = async () => await faucet.DispenseAsync(addr, amount);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*already in progress*not re-submitting*");

        (await store.GetAsync(key, CancellationToken.None))!.State.Should().Be(IdempotencyState.InProgress);
    }

    [Fact]
    public void KeyDerivation_IsDeterministic_SameInputsSameKey_DifferentInputsDifferentKey()
    {
        // Same (address, amount) ⇒ identical key (a retried topup dedupes).
        DerivedKey("ADDR_X", 5m).Should().Be(DerivedKey("ADDR_X", 5m));

        // Different amount ⇒ different key (a genuinely different dispense is
        // NOT wrongly suppressed).
        DerivedKey("ADDR_X", 5m).Should().NotBe(DerivedKey("ADDR_X", 6m));

        // Different recipient ⇒ different key.
        DerivedKey("ADDR_X", 5m).Should().NotBe(DerivedKey("ADDR_Y", 5m));

        // Format sanity: matches OperationIdGenerator's op_{chain}_{type}_{hex}.
        DerivedKey("ADDR_X", 5m).Should().StartWith("op_algorand_faucet-dispense_");
    }

    [Fact]
    public async Task DispenseAsync_SameKeyEndToEnd_RetriedDispenseDedupesViaStore()
    {
        // No pre-seed: the first call wins and enters the submit path, failing
        // offline (GetNetworkConfig throws) so its catch records Failed. The
        // same-key retry then hits the Failed dedup gate — one submit attempt.
        const string addr = "RECIPIENT_ADDRESS_DDD";
        const decimal amount = 3m;

        var (faucet, store) = BuildFaucet();
        var key = DerivedKey(addr, amount);

        // Winner enters submit path, fails offline at network-config resolution.
        var first = async () => await faucet.DispenseAsync(addr, amount);
        await first.Should().ThrowAsync<Exception>(
            "the claim winner reaches the submit path and fails OFFLINE at network-config resolution (no node contacted)");

        var afterFirst = await store.GetAsync(key, CancellationToken.None);
        afterFirst!.State.Should().Be(IdempotencyState.Failed,
            "the winner's catch records terminal Failed so a same-key retry will not blindly re-broadcast");

        var second = async () => await faucet.DispenseAsync(addr, amount);
        (await second.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*prior faucet dispense*failed*");

        store.RecordCount.Should().Be(1, "the retry reuses the same logical key — no second claim row");
    }

    [Fact]
    public async Task DispenseAsync_ConcurrentIdenticalDispense_ExactlyOneSubmitAttempt()
    {
        // INSERT-WINS gives exactly one winner; it enters the submit path and
        // fails offline, losers never enter it ⇒ one submit attempt.
        const int concurrency = 12;
        const string addr = "RECIPIENT_ADDRESS_EEE";
        const decimal amount = 4m;

        var (faucet, store) = BuildFaucet();
        var key = DerivedKey(addr, amount);

        using var barrier = new Barrier(concurrency);

        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            try
            {
                await faucet.DispenseAsync(addr, amount);
                return (threw: false, type: "");
            }
            catch (Exception ex)
            {
                return (threw: true, type: ex.GetType().Name);
            }
        })).ToArray();

        var outcomes = await Task.WhenAll(tasks);

        store.RecordCount.Should().Be(1, "all N concurrent identical dispenses collapse to ONE idempotency claim");

        var rec = await store.GetAsync(key, CancellationToken.None);
        rec!.State.Should().Be(IdempotencyState.Failed,
            "exactly one winner entered the submit path (failing offline) — losers never did, so there is at most one submit attempt");

        outcomes.Should().OnlyContain(o => o.threw,
            "the winner fails offline at network-config resolution; losers throw the dedup conflict, so no caller can succeed without a node");
    }
}
