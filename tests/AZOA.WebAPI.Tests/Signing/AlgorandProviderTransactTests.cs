using System.Net;
using System.Text;
using System.Text.Json;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using Xunit;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// signing-core-keystone Phase 3: AlgorandProvider build → sign → submit →
/// confirm flow, driven against an in-process Algod stub. Proves asset-id
/// extraction on mint and that a post-broadcast failure is NOT auto-retried
/// (RetrySafety.Broadcast — double-spend guard).
/// </summary>
public class AlgorandProviderTransactTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly AlgoAccount _platform = new();

    // Recorded server-side state for assertions.
    private int _submitCount;
    private string? _lastGenesisHashB64 = "SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=";
    // Captures the raw msgpack body POSTed to /v2/transactions for wire-byte assertions.
    private volatile byte[]? _lastSubmittedBody;

    public AlgorandProviderTransactTests()
    {
        // Bind to a free loopback port.
        var port = GetFreePort();
        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
    }

    private AlgorandProvider NewProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = "unit-test-wallet-encryption-key-0123456789",
                ["AZOA:Algorand:PlatformMnemonic"] = _platform.ToMnemonic(),
                ["Blockchain:DefaultNetwork"] = "devnet",
                ["Blockchain:Chains:0:ChainType"] = "Algorand",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = _baseUrl,
                ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "5000",
            })
            .Build();

        var keyService = new AZOA.WebAPI.Core.WalletKeyService(config);
        var signerFactory = new TransactionSignerFactory(new[] { new AlgorandTransactionSigner() });
        return new AlgorandProvider(config, NullLogger<AlgorandProvider>.Instance, signerFactory, keyService);
    }

    [Fact]
    public async Task CreateASA_builds_signs_submits_and_extracts_real_asset_id()
    {
        const long assetId = 7777;
        using var _ = RunStub(confirmedRound: 42, assetIndex: assetId, poolError: null);

        var provider = NewProvider();
        var result = await provider.CreateASAAsync(
            "Test Asset", "TST", total: 1000, decimals: 0,
            _platform.Address.EncodeAsString(), _platform.Address.EncodeAsString(),
            _platform.Address.EncodeAsString(), _platform.Address.EncodeAsString(),
            _platform.Address.EncodeAsString());

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be(assetId.ToString(), "the confirmed asset index must be returned");
        _submitCount.Should().Be(1);
    }

    [Fact]
    public async Task Mint_delegates_to_CreateASA_and_returns_asset_id()
    {
        const long assetId = 9001;
        using var _ = RunStub(confirmedRound: 10, assetIndex: assetId, poolError: null);

        var provider = NewProvider();
        var result = await provider.MintAsync(
            "ipfs://meta", amount: 1, assetType: "BADGE",
            _platform.Address.EncodeAsString());

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be(assetId.ToString());
    }

    [Fact]
    public async Task Soulbound_mint_succeeds_and_is_fully_parameterized()
    {
        const long assetId = 555;
        using var _ = RunStub(confirmedRound: 5, assetIndex: assetId, poolError: null);

        var provider = NewProvider();
        var result = await provider.CreateSoulboundAsaAsync(
            name: "Caller Supplied Name", unitName: "SBT",
            platformAddress: _platform.Address.EncodeAsString(),
            url: "https://caller.example/credential/1");

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be(assetId.ToString());

        // Wire-byte assertion: decode the POSTed msgpack and verify the soulbound
        // constraint fields reached the wire with the correct values.
        _lastSubmittedBody.Should().NotBeNullOrEmpty("a signed transaction must have been posted");
        var signedTxn = Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>(_lastSubmittedBody!);
        signedTxn.Should().NotBeNull();
        signedTxn.Sig.Should().NotBeNull("the transaction must carry a real Ed25519 signature");
        var createTxn = signedTxn.Tx.Should().BeOfType<AssetCreateTransaction>().Subject;
        createTxn.AssetParams.Total.Should().Be(1UL, "soulbound ASA is non-divisible single-supply");
        createTxn.AssetParams.Decimals.Should().Be(0UL, "soulbound ASA has zero decimals");
        createTxn.AssetParams.DefaultFrozen.Should().BeTrue("soulbound ASA is frozen at issuance");
        var platformAddr = _platform.Address.EncodeAsString();
        createTxn.AssetParams.Manager?.EncodeAsString().Should().Be(platformAddr, "platform is ASA manager");
        createTxn.AssetParams.Freeze?.EncodeAsString().Should().Be(platformAddr, "platform is freeze authority");
        createTxn.AssetParams.Clawback?.EncodeAsString().Should().Be(platformAddr, "platform is clawback authority");
    }

    [Fact]
    public async Task Transfer_builds_signs_and_submits()
    {
        const ulong tokenId = 12345UL;
        const ulong transferAmount = 1UL;
        using var _ = RunStub(confirmedRound: 3, assetIndex: null, poolError: null);

        var provider = NewProvider();
        var toAddr = _platform.Address.EncodeAsString();
        var result = await provider.TransferAsync(
            tokenId: tokenId.ToString(),
            fromAddress: _platform.Address.EncodeAsString(),
            toAddress: toAddr,
            amount: transferAmount);

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().NotBeNullOrWhiteSpace("a confirmed tx id is returned");
        _submitCount.Should().Be(1);

        // Wire-byte assertion: decode the POSTed msgpack and verify the correct
        // asset id, amount, and receiver reached the wire.
        _lastSubmittedBody.Should().NotBeNullOrEmpty("a signed transaction must have been posted");
        var signedTxn = Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>(_lastSubmittedBody!);
        signedTxn.Should().NotBeNull();
        signedTxn.Sig.Should().NotBeNull("the transaction must carry a real Ed25519 signature");
        var xferTxn = signedTxn.Tx.Should().BeOfType<AssetTransferTransaction>().Subject;
        xferTxn.XferAsset.Should().Be(tokenId, "the correct ASA id must be set on the transfer");
        xferTxn.AssetAmount.Should().Be((ulong)transferAmount, "the correct amount must be set on the transfer");
        xferTxn.AssetReceiver.EncodeAsString().Should().Be(toAddr, "the correct receiver must be set on the transfer");
    }

    [Fact]
    public async Task Broadcast_failure_is_not_retried_double_spend_guard()
    {
        // Stub returns 500 on submit. RetrySafety.Broadcast must NOT retry an
        // ambiguous post-send 5xx — exactly one submit attempt is made.
        using var _ = RunStub(confirmedRound: 1, assetIndex: 1, poolError: null, failSubmitWith: 500);

        var provider = NewProvider();
        var result = await provider.TransferAsync(
            "12345", _platform.Address.EncodeAsString(), _platform.Address.EncodeAsString(), 1);

        result.IsError.Should().BeTrue();
        _submitCount.Should().Be(1, "a broadcast 5xx is ambiguous and must not be auto-retried");
    }

    [Fact]
    public async Task Transfer_rejects_invalid_recipient_address()
    {
        var provider = NewProvider();
        var result = await provider.TransferAsync(
            "12345", _platform.Address.EncodeAsString(), "NOT-A-VALID-ADDRESS", 1);

        result.IsError.Should().BeTrue();
    }

    // ─── In-process Algod stub ───

    private IDisposable RunStub(long confirmedRound, long? assetIndex, string? poolError, int? failSubmitWith = null)
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
                        WriteJson(ctx, new
                        {
                            fee = 0,
                            @class = "",
                        }, extra: new Dictionary<string, object?>
                        {
                            ["min-fee"] = 1000,
                            ["last-round"] = 100,
                            ["genesis-id"] = "devnet-v1.0",
                            ["genesis-hash"] = _lastGenesisHashB64,
                        });
                    }
                    else if (path == "/v2/transactions")
                    {
                        // Capture the raw msgpack body for wire-byte assertions.
                        using var ms = new System.IO.MemoryStream();
                        await ctx.Request.InputStream.CopyToAsync(ms);
                        _lastSubmittedBody = ms.ToArray();

                        Interlocked.Increment(ref _submitCount);
                        if (failSubmitWith.HasValue)
                        {
                            ctx.Response.StatusCode = failSubmitWith.Value;
                            ctx.Response.Close();
                        }
                        else
                        {
                            WriteJson(ctx, new { txId = "STUBTXID" });
                        }
                    }
                    else if (path.Contains("/v2/transactions/pending/"))
                    {
                        WriteJson(ctx, new { }, extra: new Dictionary<string, object?>
                        {
                            ["confirmed-round"] = confirmedRound,
                            ["asset-index"] = assetIndex,
                            ["pool-error"] = poolError ?? "",
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

    private static void WriteJson(HttpListenerContext ctx, object body, Dictionary<string, object?>? extra = null)
    {
        // Merge into a single JSON object (the typed body fields are placeholders;
        // the Algod field names live in `extra`).
        var dict = new Dictionary<string, object?>();
        foreach (var p in body.GetType().GetProperties())
            dict[p.Name] = p.GetValue(body);
        if (extra != null)
            foreach (var kv in extra) dict[kv.Key] = kv.Value;

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
