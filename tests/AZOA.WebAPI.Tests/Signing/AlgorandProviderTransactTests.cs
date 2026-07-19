using System.Net;
using System.Text.Json;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Services.Signing;
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
public class AlgorandProviderTransactTests
{
    private readonly AlgoAccount _platform = new();

    // Recorded server-side state for assertions.
    private int _submitCount;
    private string? _lastGenesisHashB64 = "SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=";
    // Captures the raw msgpack body POSTed to /v2/transactions for wire-byte assertions.
    private volatile byte[]? _lastSubmittedBody;

    private AlgorandProvider NewProvider(HttpMessageHandler? handler = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = "unit-test-wallet-encryption-key-0123456789",
                ["AZOA:Algorand:PlatformMnemonic"] = _platform.ToMnemonic(),
                ["Blockchain:DefaultNetwork"] = "devnet",
                ["Blockchain:Chains:0:ChainType"] = "Algorand",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://algod.test/",
                ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "1000",
            })
            .Build();

        var keyService = new AZOA.WebAPI.Services.Signing.WalletKeyService(config);
        var signerFactory = new TransactionSignerFactory(new[] { new AlgorandTransactionSigner() });
        return new AlgorandProvider(
            config,
            NullLogger<AlgorandProvider>.Instance,
            signerFactory,
            keyService,
            custodyService: null,
            custodyScopeFactory: null,
            faucet: null,
            httpMessageHandler: handler);
    }

    [Fact]
    public async Task CreateASA_builds_signs_submits_and_extracts_real_asset_id()
    {
        const long assetId = 7777;
        using var stub = RunStub(confirmedRound: 42, assetIndex: assetId, poolError: null);

        var provider = NewProvider(stub);
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
        using var stub = RunStub(confirmedRound: 10, assetIndex: assetId, poolError: null);

        var provider = NewProvider(stub);
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
        using var stub = RunStub(confirmedRound: 5, assetIndex: assetId, poolError: null);

        var provider = NewProvider(stub);
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
        using var stub = RunStub(confirmedRound: 3, assetIndex: null, poolError: null);

        var provider = NewProvider(stub);
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
        using var stub = RunStub(confirmedRound: 1, assetIndex: 1, poolError: null, failSubmitWith: 500);

        var provider = NewProvider(stub);
        var result = await provider.TransferAsync(
            "12345", _platform.Address.EncodeAsString(), _platform.Address.EncodeAsString(), 1);

        result.IsError.Should().BeTrue();
        _submitCount.Should().Be(1, "a broadcast 5xx is ambiguous and must not be auto-retried");
    }

    [Fact]
    public async Task BurnWrapped_fails_closed_before_any_broadcast()
    {
        // Algorand has no reviewed canonical wrapped-asset lifecycle yet, so the
        // adapter must reject before it reaches any submission or confirmation seam.
        using var stub = RunStub(
            confirmedRound: 0,
            assetIndex: null,
            poolError: "TransactionPool.Remember: cannot destroy asset: creator is holding only part of the total 1000000");

        var provider = NewProvider(stub);
        var result = await provider.BurnWrappedAsync(
            tokenId: "12345", amount: 1, sourceChain: "Solana",
            sourceRecipient: _platform.Address.EncodeAsString(),
            walletAddress: _platform.Address.EncodeAsString());

        result.IsError.Should().BeTrue("the Algorand adapter does not implement a launch-safe wrapped burn");
        result.Message.Should().Contain("disabled");
        _submitCount.Should().Be(0, "fail-closed bridge primitives must never reach the network");
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

    private StubScope RunStub(long confirmedRound, long? assetIndex, string? poolError, int? failSubmitWith = null) =>
        new(this, confirmedRound, assetIndex, poolError, failSubmitWith);

    private static HttpResponseMessage JsonResponse(object body, Dictionary<string, object?>? extra = null)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var p in body.GetType().GetProperties())
            dict[p.Name] = p.GetValue(body);
        if (extra != null)
            foreach (var kv in extra) dict[kv.Key] = kv.Value;

        var content = new StringContent(JsonSerializer.Serialize(dict), System.Text.Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class StubScope : HttpMessageHandler
    {
        private readonly AlgorandProviderTransactTests _owner;
        private readonly long _confirmedRound;
        private readonly long? _assetIndex;
        private readonly string? _poolError;
        private readonly int? _failSubmitWith;

        public StubScope(
            AlgorandProviderTransactTests owner,
            long confirmedRound,
            long? assetIndex,
            string? poolError,
            int? failSubmitWith)
        {
            _owner = owner;
            _confirmedRound = confirmedRound;
            _assetIndex = assetIndex;
            _poolError = poolError;
            _failSubmitWith = failSubmitWith;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v2/transactions/params", StringComparison.Ordinal))
            {
                return JsonResponse(new { fee = 0, @class = "" }, extra: new Dictionary<string, object?>
                {
                    ["min-fee"] = 1000,
                    ["last-round"] = 100,
                    ["genesis-id"] = "devnet-v1.0",
                    ["genesis-hash"] = _owner._lastGenesisHashB64,
                });
            }

            if (path == "/v2/transactions")
            {
                _owner._lastSubmittedBody = request.Content is null
                    ? Array.Empty<byte>()
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken);

                Interlocked.Increment(ref _owner._submitCount);
                return _failSubmitWith.HasValue
                    ? new HttpResponseMessage((HttpStatusCode)_failSubmitWith.Value)
                    : JsonResponse(new { txId = "STUBTXID" });
            }

            if (path.Contains("/v2/transactions/pending/", StringComparison.Ordinal))
            {
                return JsonResponse(new { }, extra: new Dictionary<string, object?>
                {
                    ["confirmed-round"] = _confirmedRound,
                    ["asset-index"] = _assetIndex,
                    ["pool-error"] = _poolError ?? "",
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
