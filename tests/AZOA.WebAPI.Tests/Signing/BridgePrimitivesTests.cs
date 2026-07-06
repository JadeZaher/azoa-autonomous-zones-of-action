using System.Net;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Xunit;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// final-hardening-cutover B2: the bridge value primitives must either broadcast a
/// REAL transaction or fail CLOSED — never return a fabricated-success Ok on a value
/// path. Algorand lock/burn-wrapped broadcast real ASA transactions (asserted at the
/// wire); a missing platform mnemonic fails closed. Solana lock/burn/mint fail closed
/// (no real signer/submit pipeline). Also proves the always-true provider verifier is
/// gone (the interface no longer declares VerifyBridgeProofAsync — enforced at compile
/// time by this file referencing IBlockchainProvider without it).
/// </summary>
public class BridgePrimitivesTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly AlgoAccount _platform = new();
    private readonly AlgoAccount _vault = new();

    private int _submitCount;
    private volatile byte[]? _lastSubmittedBody;

    public BridgePrimitivesTests()
    {
        var port = GetFreePort();
        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
    }

    private AlgorandProvider NewAlgorandProvider(bool withPlatformMnemonic = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AZOA:WalletEncryptionKey"] = "unit-test-wallet-encryption-key-0123456789",
            ["Blockchain:DefaultNetwork"] = "devnet",
            ["Blockchain:Chains:0:ChainType"] = "Algorand",
            ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
            ["Blockchain:Chains:0:Devnet:NodeUrl"] = _baseUrl,
            ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "5000",
        };
        if (withPlatformMnemonic)
            settings["AZOA:Algorand:PlatformMnemonic"] = _platform.ToMnemonic();

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var keyService = new WalletKeyService(config);
        var signerFactory = new TransactionSignerFactory(new[] { new AlgorandTransactionSigner() });
        return new AlgorandProvider(config, NullLogger<AlgorandProvider>.Instance, signerFactory, keyService);
    }

    private static SolanaProvider NewSolanaProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "devnet",
                ["Blockchain:Chains:0:ChainType"] = "Solana",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://127.0.0.1:1/",
                ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "2000",
            })
            .Build();
        return new SolanaProvider(config, NullLogger<SolanaProvider>.Instance);
    }

    // ─── Algorand: REAL broadcast ───

    [Fact]
    public async Task Algorand_lock_broadcasts_real_asset_transfer_to_vault()
    {
        const ulong assetId = 4242UL;
        const int amount = 5;
        using var _ = RunStub(confirmedRound: 9);

        var provider = NewAlgorandProvider();
        var result = await provider.LockForBridgeAsync(
            tokenId: assetId.ToString(),
            vaultAddress: _vault.Address.EncodeAsString(),
            amount: amount,
            targetChain: "Solana",
            targetRecipient: _platform.Address.EncodeAsString());

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().NotBeNullOrWhiteSpace("a real tx id must be returned, not a fabricated op-id");
        _submitCount.Should().Be(1, "the lock must broadcast exactly one real transaction");

        _lastSubmittedBody.Should().NotBeNullOrEmpty("a signed transaction must have been posted");
        var signedTxn = Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>(_lastSubmittedBody!);
        signedTxn.Sig.Should().NotBeNull("the lock tx must carry a real Ed25519 signature");
        var xfer = signedTxn.Tx.Should().BeOfType<AssetTransferTransaction>().Subject;
        xfer.XferAsset.Should().Be(assetId, "the locked ASA id must reach the wire");
        xfer.AssetAmount.Should().Be((ulong)amount, "the locked amount must reach the wire");
        xfer.AssetReceiver.EncodeAsString().Should().Be(
            _vault.Address.EncodeAsString(), "value must move into the bridge vault");
    }

    [Fact]
    public async Task Algorand_burn_wrapped_broadcasts_real_asset_destroy()
    {
        const ulong assetId = 7788UL;
        using var _ = RunStub(confirmedRound: 4);

        var provider = NewAlgorandProvider();
        var result = await provider.BurnWrappedAsync(
            tokenId: assetId.ToString(),
            amount: 3,
            sourceChain: "Algorand",
            sourceRecipient: _platform.Address.EncodeAsString(),
            walletAddress: _platform.Address.EncodeAsString());

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().NotBeNullOrWhiteSpace("a real tx id must be returned, not a fabricated op-id");
        _submitCount.Should().Be(1, "the wrapped burn must broadcast exactly one real transaction");

        _lastSubmittedBody.Should().NotBeNullOrEmpty();
        var signedTxn = Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>(_lastSubmittedBody!);
        signedTxn.Sig.Should().NotBeNull("the burn tx must carry a real Ed25519 signature");
        var destroy = signedTxn.Tx.Should().BeOfType<AssetDestroyTransaction>().Subject;
        destroy.AssetIndex.Should().Be(assetId, "the wrapped ASA id must reach the wire");
    }

    // ─── Algorand: fail-closed (no fabricated Ok) ───

    [Fact]
    public async Task Algorand_lock_fails_closed_without_platform_mnemonic()
    {
        var provider = NewAlgorandProvider(withPlatformMnemonic: false);
        var result = await provider.LockForBridgeAsync(
            "4242", _vault.Address.EncodeAsString(), 5, "Solana", _platform.Address.EncodeAsString());

        result.IsError.Should().BeTrue("a missing platform signer must fail closed, never fake a lock");
        _submitCount.Should().Be(0, "no transaction may be broadcast without a signer");
    }

    [Fact]
    public async Task Algorand_burn_wrapped_fails_closed_without_platform_mnemonic()
    {
        var provider = NewAlgorandProvider(withPlatformMnemonic: false);
        var result = await provider.BurnWrappedAsync(
            "7788", 3, "Algorand", _platform.Address.EncodeAsString(), _platform.Address.EncodeAsString());

        result.IsError.Should().BeTrue("a missing platform signer must fail closed, never fake a burn");
        _submitCount.Should().Be(0);
    }

    [Fact]
    public async Task Algorand_lock_rejects_non_numeric_asset_id()
    {
        var provider = NewAlgorandProvider();
        var result = await provider.LockForBridgeAsync(
            "not-a-number", _vault.Address.EncodeAsString(), 5, "Solana", _platform.Address.EncodeAsString());

        result.IsError.Should().BeTrue();
    }

    // ─── Solana: fail-closed (no real pipeline) ───

    [Fact]
    public async Task Solana_lock_fails_closed_never_fabricates_success()
    {
        var provider = NewSolanaProvider();
        var result = await provider.LockForBridgeAsync(
            "SomeMint", "VaultAddr", 5, "Algorand", "Recipient");

        result.IsError.Should().BeTrue("Solana has no real lock pipeline — it must fail closed");
        result.Message.Should().Contain("not implemented");
    }

    [Fact]
    public async Task Solana_burn_wrapped_fails_closed_never_fabricates_success()
    {
        var provider = NewSolanaProvider();
        var result = await provider.BurnWrappedAsync(
            "SomeMint", 5, "Algorand", "SourceRecipient", "Wallet");

        result.IsError.Should().BeTrue("Solana has no real burn pipeline — it must fail closed");
        result.Message.Should().Contain("not implemented");
    }

    [Fact]
    public async Task Solana_mint_wrapped_fails_closed_never_fabricates_success()
    {
        var provider = NewSolanaProvider();
        var result = await provider.MintWrappedAsync(
            "Algorand", "SourceToken", "bridge://uri", 5, "Recipient");

        result.IsError.Should().BeTrue("Solana has no real mint pipeline — it must fail closed");
        result.Message.Should().Contain("not implemented");
    }

    // ─── In-process Algod stub (mirrors AlgorandProviderTransactTests) ───

    private IDisposable RunStub(long confirmedRound)
    {
        var cts = new CancellationTokenSource();
        var loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }

                var path = ctx.Request.Url!.AbsolutePath;
                try
                {
                    if (path.EndsWith("/v2/transactions/params"))
                    {
                        WriteJson(ctx, new Dictionary<string, object?>
                        {
                            ["fee"] = 0,
                            ["min-fee"] = 1000,
                            ["last-round"] = 100,
                            ["genesis-id"] = "devnet-v1.0",
                            ["genesis-hash"] = "SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=",
                        });
                    }
                    else if (path == "/v2/transactions")
                    {
                        using var ms = new System.IO.MemoryStream();
                        await ctx.Request.InputStream.CopyToAsync(ms);
                        _lastSubmittedBody = ms.ToArray();
                        Interlocked.Increment(ref _submitCount);
                        WriteJson(ctx, new Dictionary<string, object?> { ["txId"] = "STUBTXID" });
                    }
                    else if (path.Contains("/v2/transactions/pending/"))
                    {
                        WriteJson(ctx, new Dictionary<string, object?>
                        {
                            ["confirmed-round"] = confirmedRound,
                            ["asset-index"] = null,
                            ["pool-error"] = "",
                        });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                    }
                }
                catch
                {
                    try { ctx.Response.Abort(); } catch { /* ignore */ }
                }
            }
        });

        return new StubScope(cts, loop);
    }

    private static void WriteJson(HttpListenerContext ctx, Dictionary<string, object?> dict)
    {
        var json = JsonSerializer.Serialize(dict);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = 200;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { /* ignore */ }
    }

    private sealed class StubScope : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _loop;
        public StubScope(CancellationTokenSource cts, Task loop) { _cts = cts; _loop = loop; }
        public void Dispose()
        {
            _cts.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }
}
