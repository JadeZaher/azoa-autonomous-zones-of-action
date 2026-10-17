using System.Net;
using System.Text.Json;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Moq;
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
public class AlgorandProviderTransactTests : IDisposable
{
    private readonly AlgoAccount _platform = new();
    private readonly ServiceProvider _preparations = new ServiceCollection()
        .AddSingleton<INativePreparationStore, InMemoryNativePreparationStore>().BuildServiceProvider();
    public void Dispose() => _preparations.Dispose();

    // Recorded server-side state for assertions.
    private int _submitCount;
    private int _signatureCount;
    private bool _pauseFirstSignature;
    private readonly TaskCompletionSource _signatureEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseSignature = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ulong _indexedRound = 2000;
    private object? _indexedRoundOverride;
    private string? _indexContinuation;
    private long _nodeRound = 100;
    private object[] _indexedTransactions = [];
    private string? _indexedGenesis;
    private Dictionary<string, object?>? _custodyAsset;
    private readonly Dictionary<string, Dictionary<string, object?>> _custodyAccounts = new();
    private SigningContext? _recipientSigningContext;
    private AlgoAccount? _recipientSigningAccount;

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
                ["Blockchain:Chains:0:Devnet:IndexerUrl"] = "http://indexer.test/",
                ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "1000",
            })
            .Build();

        var keyService = new AZOA.WebAPI.Services.Signing.WalletKeyService(config);
        var signerFactory = new TransactionSignerFactory(new[] { new AlgorandTransactionSigner() });
        var custody = new Mock<IKeyCustodyService>(MockBehavior.Strict);
        custody.Setup(service => service.WithPlatformSigningKeyAsync(true, It.IsAny<SigningContext>(),
                It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .Returns(async (bool _, SigningContext _, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
            {
                if (Interlocked.Increment(ref _signatureCount) == 1 && _pauseFirstSignature)
                {
                    _signatureEntered.TrySetResult();
                    await _releaseSignature.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                return AZOAResult<AZOAResult<byte[]>>.Success(await sign(_platform.KeyPair.ClearTextPrivateKey));
            });
        custody.Setup(service => service.WithSigningKeyAsync(
                It.Is<SigningContext>(context => _recipientSigningContext.HasValue && context == _recipientSigningContext.Value),
                It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .Returns(async (SigningContext _, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
            {
                Interlocked.Increment(ref _signatureCount);
                return AZOAResult<AZOAResult<byte[]>>.Success(await sign(_recipientSigningAccount!.KeyPair.ClearTextPrivateKey));
            });
        return new AlgorandProvider(
            config,
            NullLogger<AlgorandProvider>.Instance,
            signerFactory,
            keyService,
            custodyService: custody.Object,
            custodyScopeFactory: _preparations.GetRequiredService<IServiceScopeFactory>(),
            faucet: null,
            httpMessageHandler: handler);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("acceptance")]
    [InlineData("foreign-authority")]
    [InlineData("rekeyed")]
    [InlineData("malformed-balance")]
    public async Task CustodyQuoteUsesCurrentMechanicsAndSeparatesRecipientCostsWithoutSigning(string state)
    {
        var sender = _platform.Address.EncodeAsString();
        var recipient = new AlgoAccount().Address.EncodeAsString();
        _custodyAsset = new() { ["creator"] = sender, ["total"] = 1000UL, ["decimals"] = 0,
            ["manager"] = state == "foreign-authority" ? recipient : sender, ["default-frozen"] = false };
        _custodyAccounts[sender] = new() { ["address"] = sender, ["amount"] = 1_000_000UL,
            ["min-balance"] = 200_000UL, ["assets"] = new[] { new Dictionary<string, object?>
                { ["asset-id"] = 123UL, ["amount"] = 500UL, ["is-frozen"] = false } } };
        _custodyAccounts[recipient] = new() { ["address"] = recipient, ["amount"] = 1_000_000UL,
            ["min-balance"] = 100_000UL, ["assets"] = state == "acceptance" ? Array.Empty<object>()
                : new object[] { new Dictionary<string, object?> { ["asset-id"] = 123UL, ["amount"] = 0UL, ["is-frozen"] = false } } };
        if (state == "rekeyed") _custodyAccounts[sender]["auth-addr"] = recipient;
        if (state == "malformed-balance") _custodyAccounts[sender]["amount"] = "1000000";
        using var stub = RunStub(0, null, null);
        var result = await NewProvider(stub).QuoteValueCustodyAsync(new("Algorand", ChainNetwork.Devnet,
            "123", sender, recipient, "5", sender), default);
        if (state is "ready" or "acceptance")
        {
            result.IsError.Should().BeFalse(result.Message);
            result.Result!.RecipientReady.Should().Be(state == "ready");
            result.Result.TransferNetworkFeeBaseUnits.Should().Be("1000");
            result.Result.AcceptanceNetworkFeeBaseUnits.Should().Be(state == "ready" ? "0" : "1000");
            result.Result.AcceptanceMinimumBalanceBaseUnits.Should().Be(state == "ready" ? "0" : "100000");
        }
        else result.IsError.Should().BeTrue();
        _signatureCount.Should().Be(0);
        _submitCount.Should().Be(0);
    }

    [Fact]
    public async Task RecipientAcceptanceUsesActualRecipientAndRecoversOneExactWire()
    {
        var sender = _platform.Address.EncodeAsString();
        _recipientSigningAccount = new AlgoAccount();
        var recipient = _recipientSigningAccount.Address.EncodeAsString();
        _recipientSigningContext = new SigningContext(Guid.NewGuid(), Guid.NewGuid(), false, Scope: AzoaScopes.TransferSign);
        _custodyAsset = new() { ["creator"] = sender, ["total"] = 1000UL, ["decimals"] = 0, ["manager"] = sender };
        _custodyAccounts[sender] = new() { ["address"] = sender, ["amount"] = 1_000_000UL, ["min-balance"] = 200_000UL,
            ["assets"] = new[] { new Dictionary<string, object?> { ["asset-id"] = 123UL, ["amount"] = 500UL, ["is-frozen"] = false } } };
        _custodyAccounts[recipient] = new() { ["address"] = recipient, ["amount"] = 1_000_000UL,
            ["min-balance"] = 100_000UL, ["assets"] = Array.Empty<object>() };
        using var stub = RunStub(0, null, null);
        var provider = NewProvider(stub);
        var quote = (await provider.QuoteValueCustodyAsync(new("Algorand", ChainNetwork.Devnet, "123",
            sender, recipient, "5", sender), default)).Result!;
        var intent = new ValueAcceptanceIntent("Algorand", ChainNetwork.Devnet, "123", recipient, "hold:accept:0",
            _recipientSigningContext.Value, new(1, "hold-command", "accept", 0, quote, recipient, "1000"));
        (await provider.PrepareValueAcceptanceAsync(intent with { SigningContext = SigningContext.PlatformFor("Algorand") }, default))
            .IsError.Should().BeTrue();
        _signatureCount.Should().Be(0);
        var first = (await provider.PrepareValueAcceptanceAsync(intent, default)).Result!;
        var second = (await provider.PrepareValueAcceptanceAsync(intent, default)).Result!;
        first.Should().BeEquivalentTo(second);
        _signatureCount.Should().Be(1);
        _submitCount.Should().Be(0);
        var signed = AlgorandSignedEnvelope.DecodeCanonical(first.SignedTransactionBase64);
        signed.Tx.Should().BeOfType<AssetAcceptTransaction>();
        signed.Tx.Sender.EncodeAsString().Should().Be(recipient);
        var tx = signed.Tx;
        _indexedTransactions = [new Dictionary<string, object?> {
            ["id"] = first.TransactionId, ["sender"] = recipient, ["tx-type"] = "axfer", ["confirmed-round"] = tx.FirstValid,
            ["fee"] = tx.Fee, ["first-valid"] = tx.FirstValid, ["last-valid"] = tx.LastValid,
            ["note"] = Convert.ToBase64String(tx.Note),
            ["asset-transfer-transaction"] = new Dictionary<string, object?> { ["asset-id"] = 123UL, ["receiver"] = recipient, ["amount"] = 0UL } }];
        await provider.SubmitValueAcceptanceAsync(first, default);
        _submitCount.Should().Be(1);
        (await provider.ObserveValueAcceptanceAsync(first, default)).Result.Should().Be(ChainConfirmation.Confirmed);
        var wire = Convert.FromBase64String(first.SignedTransactionBase64);
        var corrupt = first with { SignedTransactionBase64 = Convert.ToBase64String(wire.Concat(wire).ToArray()) };
        await FluentActions.Awaiting(() => provider.SubmitValueAcceptanceAsync(corrupt, default)).Should().ThrowAsync<InvalidOperationException>();
        _submitCount.Should().Be(1);
        _signatureCount.Should().Be(1);
    }

    [Fact]
    public async Task OverlappingPreparationWinsOneUnsignedPlanAndNeverSubmits()
    {
        using var stub = RunStub(0, null, null);
        var provider = NewProvider(stub);
        _pauseFirstSignature = true;
        var intent = new ValueTransferIntent("Algorand", ChainNetwork.Devnet, "123",
            _platform.Address.EncodeAsString(), new AlgoAccount().Address.EncodeAsString(), "500",
            SigningContext.PlatformFor("Algorand"), "value:overlap:0");
        var first = provider.PrepareValueTransferAsync(intent, default);
        try
        {
            await _signatureEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = await NewProvider(stub).PrepareValueTransferAsync(intent, default);
            second.IsError.Should().BeFalse(second.Message);
            _releaseSignature.TrySetResult();
            var completed = await first.WaitAsync(TimeSpan.FromSeconds(10));
            completed.Result.Should().Be(second.Result);
            var signatures = _signatureCount;
            (await NewProvider(stub).PrepareValueTransferAsync(intent, default)).Result.Should().Be(second.Result);
            _signatureCount.Should().Be(signatures);
            (await provider.PrepareValueTransferAsync(intent with { QuantityBaseUnits = "501" }, default)).IsError.Should().BeTrue();
            _submitCount.Should().Be(0);
        }
        finally { _releaseSignature.TrySetResult(); }
    }

    [Fact]
    public async Task MissingPreparationKeyCannotSign()
    {
        using var stub = RunStub(0, null, null);
        var result = await NewProvider(stub).PrepareValueTransferAsync(new("Algorand", ChainNetwork.Devnet, "123",
            _platform.Address.EncodeAsString(), new AlgoAccount().Address.EncodeAsString(), "500", SigningContext.PlatformFor("Algorand")), default);
        result.IsError.Should().BeTrue();
        _signatureCount.Should().Be(0);
        _submitCount.Should().Be(0);
    }

    [Fact]
    public async Task PreparedValueTransferRoundTripsSignedWireAndReplaysOnlyOriginalIdentity()
    {
        using var stub = RunStub(0, null, null);
        var provider = NewProvider(stub);
        var recipient = new AlgoAccount().Address.EncodeAsString();
        var result = await provider.PrepareValueTransferAsync(new("Algorand", ChainNetwork.Devnet, "123",
            _platform.Address.EncodeAsString(), recipient, "500", SigningContext.PlatformFor("Algorand"), "value:roundtrip:0"), default);
        result.IsError.Should().BeFalse(result.Message);
        _submitCount.Should().Be(0);
        var prepared = JsonSerializer.Deserialize<PreparedValueTransfer>(JsonSerializer.Serialize(result.Result))!;
        var decoded = Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>(Convert.FromBase64String(prepared.SignedTransactionBase64));
        var transfer = decoded.Tx.Should().BeOfType<AssetTransferTransaction>().Subject;
        transfer.AssetReceiver.EncodeAsString().Should().Be(recipient);
        transfer.AssetAmount.Should().Be(500UL);
        decoded.Tx.TxID().Should().Be(prepared.TransactionId);
        await provider.SubmitValueTransferAsync(prepared, default);
        var first = _lastSubmittedBody!.ToArray();
        await provider.SubmitValueTransferAsync(prepared, default);
        _lastSubmittedBody.Should().Equal(first);
        _submitCount.Should().Be(2);
        var tampered = prepared with { Intent = prepared.Intent with { QuantityBaseUnits = "501" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SubmitValueTransferAsync(tampered, default));
        var original = Convert.FromBase64String(prepared.SignedTransactionBase64);
        var appended = prepared with { SignedTransactionBase64 = Convert.ToBase64String(original.Concat(original).ToArray()) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SubmitValueTransferAsync(appended, default));
        var trailing = prepared with { SignedTransactionBase64 = Convert.ToBase64String(original.Concat(new byte[] { 0 }).ToArray()) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SubmitValueTransferAsync(trailing, default));
        decoded.Sig.Bytes[0] ^= 1;
        var badSignature = prepared with { SignedTransactionBase64 = Convert.ToBase64String(Algorand.Utils.Encoder.EncodeToMsgPackOrdered(decoded)) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SubmitValueTransferAsync(badSignature, default));
        _submitCount.Should().Be(2);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PreparedValueConfirmationRequiresExactIndexedSemanticsAndGenesis(bool wrongAmount, bool wrongGenesis)
    {
        using var stub = RunStub(0, null, null);
        var provider = NewProvider(stub);
        var recipient = new AlgoAccount().Address.EncodeAsString();
        var prepared = (await provider.PrepareValueTransferAsync(new("Algorand", ChainNetwork.Devnet, "123",
            _platform.Address.EncodeAsString(), recipient, "500", SigningContext.PlatformFor("Algorand"), "value:confirmation:0"), default)).Result!;
        var transaction = Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>(Convert.FromBase64String(prepared.SignedTransactionBase64)).Tx;
        _indexedTransactions = [new Dictionary<string, object> {
            ["id"] = prepared.TransactionId, ["sender"] = prepared.Intent.Sender, ["confirmed-round"] = 105,
            ["tx-type"] = "axfer", ["fee"] = transaction.Fee!.Value, ["first-valid"] = transaction.FirstValid!.Value, ["last-valid"] = transaction.LastValid!.Value,
            ["note"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(AZOA.WebAPI.Helpers.CanonicalJson.Hash(prepared.Intent))),
            ["asset-transfer-transaction"] = new Dictionary<string, object> {
                ["asset-id"] = 123, ["amount"] = wrongAmount ? 501 : 500, ["receiver"] = recipient } }];
        if (wrongGenesis) _indexedGenesis = Convert.ToBase64String(new byte[32]);
        var observed = await provider.ObserveValueTransferAsync(prepared, default);
        if (wrongAmount || wrongGenesis) observed.IsError.Should().BeTrue();
        else observed.Result.Should().Be(ChainConfirmation.Confirmed);
        _submitCount.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartialOrMalformedIndexerEvidenceNeverProvesNoEffect(bool continuation)
    {
        using var stub = RunStub(0, null, null);
        var provider = NewProvider(stub);
        var prepared = (await provider.PrepareValueTransferAsync(new("Algorand", ChainNetwork.Devnet, "123",
            _platform.Address.EncodeAsString(), new AlgoAccount().Address.EncodeAsString(), "500",
            SigningContext.PlatformFor("Algorand"), "value:incomplete:0"), default)).Result!;
        _nodeRound = 2000;
        if (continuation) _indexContinuation = "another-page";
        else _indexedRoundOverride = "invalid-round";
        var observed = await provider.ObserveValueTransferAsync(prepared, default);
        observed.IsError.Should().BeFalse();
        observed.Result.Should().Be(ChainConfirmation.Unknown);
        _submitCount.Should().Be(0);
    }

    [Theory]
    [InlineData(2000UL, 2000L, true)]
    [InlineData(100UL, 2000L, false)]
    [InlineData(2000UL, 100L, false)]
    public async Task PreparedValueExpiryRequiresCaughtUpIndexerAndExpiredNode(ulong indexedRound, long nodeRound, bool finalAbsence)
    {
        using var stub = RunStub(0, null, null);
        var provider = NewProvider(stub);
        var prepared = (await provider.PrepareValueTransferAsync(new("Algorand", ChainNetwork.Devnet, "123",
            _platform.Address.EncodeAsString(), new AlgoAccount().Address.EncodeAsString(), "500", SigningContext.PlatformFor("Algorand"), "value:expiry:0"), default)).Result!;
        _indexedRound = indexedRound;
        _nodeRound = nodeRound;
        var observed = await provider.ObserveValueTransferAsync(prepared, default);
        observed.Result.Should().Be(finalAbsence ? ChainConfirmation.FailedOnChain : ChainConfirmation.Unknown);
        _submitCount.Should().Be(0);
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
    public void Wrapped_burn_capability_is_absent_rather_than_refused_at_runtime()
    {
        // Algorand has no reviewed canonical wrapped-asset lifecycle, so it implements no
        // IBridgeTargetLeg. There is no wrapped-burn method here at all — the previous refusal
        // body is gone, and with it the only thing that could ever have regressed into an Ok.
        var provider = NewProvider();

        provider.TryGetModule<IBridgeTargetLeg>(out var targetLeg).Should().BeFalse(
            "no canonical platform-controlled wrapped ASA lifecycle is implemented");
        targetLeg.Should().BeNull();

        provider.TryGetModule<IBridgeSourceLeg>(out var sourceLeg).Should().BeTrue(
            "lock and release ARE real and proven, and that direction asymmetry is now expressible");
        sourceLeg.Should().NotBeNull();
        _submitCount.Should().Be(0, "a capability probe must never reach the network");
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
            if (path == "/v2/assets/123" && _owner._custodyAsset is not null)
                return JsonResponse(new { index = 123UL, @params = _owner._custodyAsset });
            if (path.StartsWith("/v2/accounts/", StringComparison.Ordinal)
                && _owner._custodyAccounts.TryGetValue(path["/v2/accounts/".Length..], out var account))
                return JsonResponse(new { }, account);
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

            if (request.RequestUri.Host == "indexer.test" && path == "/v2/transactions")
                return JsonResponse(new { }, extra: new Dictionary<string, object?> {
                    ["current-round"] = _owner._indexedRoundOverride ?? _owner._indexedRound,
                    ["next-token"] = _owner._indexContinuation, ["transactions"] = _owner._indexedTransactions });
            if (request.RequestUri.Host == "indexer.test" && path.StartsWith("/v2/blocks/", StringComparison.Ordinal))
                return JsonResponse(new { }, extra: new Dictionary<string, object?> {
                    ["genesis-hash"] = _owner._indexedGenesis ?? _owner._lastGenesisHashB64 });

            if (path == "/v2/status")
                return JsonResponse(new { }, extra: new Dictionary<string, object?> { ["last-round"] = _owner._nodeRound });

            if (path == "/v2/transactions")
            {
                _owner._lastSubmittedBody = request.Content is null
                    ? Array.Empty<byte>()
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken);

                Interlocked.Increment(ref _owner._submitCount);
                return _failSubmitWith.HasValue
                    ? new HttpResponseMessage((HttpStatusCode)_failSubmitWith.Value)
                    : JsonResponse(new { txId = Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>(_owner._lastSubmittedBody).Tx.TxID() });
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
