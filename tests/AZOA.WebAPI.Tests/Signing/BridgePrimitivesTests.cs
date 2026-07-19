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
/// path. Algorand lock broadcasts a real ASA transaction (asserted at the wire), while
/// wrapped burn/mint remain disabled; a missing platform mnemonic fails closed. Solana lock/burn/mint fail closed
/// (no real signer/submit pipeline). Also proves the always-true provider verifier is
/// gone (the interface no longer declares VerifyBridgeProofAsync — enforced at compile
/// time by this file referencing IBlockchainProvider without it).
/// </summary>
public class BridgePrimitivesTests
{
    private const string BaseUrl = "http://algod.test/";
    private readonly AlgoAccount _platform = new();
    private readonly AlgoAccount _vault = new();

    private int _submitCount;
    private volatile byte[]? _lastSubmittedBody;

    private AlgorandProvider NewAlgorandProvider(
        bool withPlatformMnemonic = true,
        HttpMessageHandler? handler = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AZOA:WalletEncryptionKey"] = "unit-test-wallet-encryption-key-0123456789",
            ["Blockchain:DefaultNetwork"] = "devnet",
            ["Blockchain:Chains:0:ChainType"] = "Algorand",
            ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
            ["Blockchain:Chains:0:Devnet:NodeUrl"] = BaseUrl,
            ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "1000",
        };
        if (withPlatformMnemonic)
            settings["AZOA:Algorand:PlatformMnemonic"] = _platform.ToMnemonic();

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var keyService = new WalletKeyService(config);
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
        using var stub = RunStub(confirmedRound: 9);

        var provider = NewAlgorandProvider(handler: stub);
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
    public async Task Algorand_burn_wrapped_fails_closed_without_canonical_wrapped_asset()
    {
        const ulong assetId = 7788UL;
        var provider = NewAlgorandProvider();
        var result = await provider.BurnWrappedAsync(
            tokenId: assetId.ToString(),
            amount: 3,
            sourceChain: "Algorand",
            sourceRecipient: _platform.Address.EncodeAsString(),
            walletAddress: _platform.Address.EncodeAsString());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("disabled");
        _submitCount.Should().Be(0, "unsafe wrapped supply must never be destroyed or reported burned");
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

    [Fact]
    public async Task Algorand_mint_wrapped_fails_closed_without_canonical_platform_asa()
    {
        var provider = NewAlgorandProvider();
        var result = await provider.MintWrappedAsync(
            "Solana", "source-token", "bridge://source-token", 5, _platform.Address.EncodeAsString());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("disabled");
        provider.SupportsBridging.Should().BeFalse();
        _submitCount.Should().Be(0);
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

    private StubScope RunStub(long confirmedRound) => new(this, confirmedRound);

    private static HttpResponseMessage JsonResponse(Dictionary<string, object?> dict)
    {
        var content = new StringContent(JsonSerializer.Serialize(dict), System.Text.Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class StubScope : HttpMessageHandler
    {
        private readonly BridgePrimitivesTests _owner;
        private readonly long _confirmedRound;

        public StubScope(BridgePrimitivesTests owner, long confirmedRound)
        {
            _owner = owner;
            _confirmedRound = confirmedRound;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v2/transactions/params", StringComparison.Ordinal))
            {
                return JsonResponse(new Dictionary<string, object?>
                {
                    ["fee"] = 0,
                    ["min-fee"] = 1000,
                    ["last-round"] = 100,
                    ["genesis-id"] = "devnet-v1.0",
                    ["genesis-hash"] = "SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=",
                });
            }

            if (path == "/v2/transactions")
            {
                _owner._lastSubmittedBody = request.Content is null
                    ? Array.Empty<byte>()
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                Interlocked.Increment(ref _owner._submitCount);
                return JsonResponse(new Dictionary<string, object?> { ["txId"] = "STUBTXID" });
            }

            if (path.Contains("/v2/transactions/pending/", StringComparison.Ordinal))
            {
                return JsonResponse(new Dictionary<string, object?>
                {
                    ["confirmed-round"] = _confirmedRound,
                    ["asset-index"] = null,
                    ["pool-error"] = "",
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
