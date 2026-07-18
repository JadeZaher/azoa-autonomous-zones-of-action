using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Services.Signing;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Signing;

public sealed class AlgorandAtomicTransferGroupCapabilityTests
{
    [Fact]
    public async Task Invalid_group_input_fails_before_http_or_custody()
    {
        using var handler = new AlgodStub();
        var custody = new Mock<IKeyCustodyService>(MockBehavior.Strict);
        var provider = NewProvider(new RecordingSigner(), handler, custody.Object);
        var request = GroupRequest(
            source: "not-an-algorand-address",
            signingContext: SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()));

        var result = await provider.SubmitAtomicTransferGroupAsync(request);

        result.IsError.Should().BeTrue();
        handler.RequestCount.Should().Be(0);
        custody.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Group_uses_one_sdk_group_id_and_one_batch_broadcast()
    {
        using var handler = new AlgodStub(pendingResponse: _ => Confirmed());
        var platform = new AlgoAccount();
        var signer = new RecordingSigner();
        var provider = NewProvider(signer, handler, platform: platform);
        var request = GroupRequest(source: platform.Address.EncodeAsString());

        provider.TryGetModule<IAtomicTransferGroupModule>(out var module).Should().BeTrue();
        module!.SupportsAtomicTransferGroups.Should().BeTrue();

        var result = await module.SubmitAtomicTransferGroupAsync(request);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.State.Should().Be(AtomicTransferGroupSubmissionState.Confirmed);
        result.Result.ChainGroupId.Should().NotBeNullOrWhiteSpace();
        signer.Groups.Should().HaveCount(2);
        signer.Groups.Distinct(StringComparer.Ordinal).Should().ContainSingle();
        handler.BroadcastCount.Should().Be(1);
        handler.LastBroadcast.Should().HaveCount(2, "both signed legs are one Algod batch body");
    }

    [Fact]
    public async Task Group_sender_must_match_the_resolved_signing_key()
    {
        using var handler = new AlgodStub();
        var provider = NewProvider(new RecordingSigner(), handler, platform: new AlgoAccount());

        var result = await provider.SubmitAtomicTransferGroupAsync(
            GroupRequest(source: new AlgoAccount().Address.EncodeAsString()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("shared sender");
        handler.BroadcastCount.Should().Be(0);
    }

    [Fact]
    public async Task Real_signer_emits_decodable_signed_group_with_bound_sender()
    {
        using var handler = new AlgodStub(pendingResponse: _ => Confirmed());
        var platform = new AlgoAccount();
        var signer = new CapturingRealSigner();
        var provider = NewProvider(signer, handler, platform: platform);

        var result = await provider.SubmitAtomicTransferGroupAsync(
            GroupRequest(source: platform.Address.EncodeAsString()));

        result.IsError.Should().BeFalse(result.Message);
        signer.Envelopes.Should().HaveCount(2);
        var signed = signer.Envelopes
            .Select(Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>)
            .ToArray();
        signed.Should().OnlyContain(envelope => envelope.Sig != null);
        signed.Select(envelope => envelope.Tx.Group!.ToString()).Distinct(StringComparer.Ordinal).Should().ContainSingle();
        signed.Should().OnlyContain(envelope =>
            envelope.Tx.Sender.EncodeAsString() == platform.Address.EncodeAsString());
        handler.LastBroadcast.Should().Equal(signer.Envelopes.SelectMany(static envelope => envelope));
    }

    [Fact]
    public async Task Custody_consent_failure_never_broadcasts()
    {
        using var handler = new AlgodStub();
        var custody = new Mock<IKeyCustodyService>();
        custody.Setup(c => c.WithSigningKeyAsync(
                It.IsAny<SigningContext>(),
                It.IsAny<Func<byte[], Task<AZOAResult<byte[][]>>>>()))
            .ReturnsAsync(new AZOAResult<AZOAResult<byte[][]>>
            {
                IsError = true,
                Message = "Tenant consent grant is missing.",
            });
        var user = new AlgoAccount();
        var provider = NewProvider(new RecordingSigner(), handler, custody.Object);
        var request = GroupRequest(
            source: user.Address.EncodeAsString(),
            signingContext: SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()));

        var result = await provider.SubmitAtomicTransferGroupAsync(request);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Tenant consent");
        handler.BroadcastCount.Should().Be(0);
        custody.Verify(c => c.WithSigningKeyAsync(
            It.IsAny<SigningContext>(),
            It.IsAny<Func<byte[], Task<AZOAResult<byte[][]>>>>()), Times.Once);
    }

    [Fact]
    public async Task Confirmation_timeout_is_recoverable_with_both_transaction_ids()
    {
        using var handler = new AlgodStub(pendingResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var platform = new AlgoAccount();
        var provider = NewProvider(new RecordingSigner(), handler, platform: platform);

        var result = await provider.SubmitAtomicTransferGroupAsync(GroupRequest(source: platform.Address.EncodeAsString()));

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.State.Should().Be(AtomicTransferGroupSubmissionState.PendingConfirmation);
        result.Result.ChainGroupId.Should().NotBeNullOrWhiteSpace();
        result.Result.PrimaryTransactionId.Should().NotBeNullOrWhiteSpace();
        result.Result.TreasuryTransactionId.Should().NotBeNullOrWhiteSpace();
        result.Result.PrimaryTransactionId.Should().NotBe(result.Result.TreasuryTransactionId);
        handler.BroadcastCount.Should().Be(1);
    }

    [Fact]
    public async Task Confirmation_infrastructure_error_is_not_misclassified_as_pending()
    {
        using var handler = new AlgodStub(pendingResponse: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var platform = new AlgoAccount();
        var provider = NewProvider(new RecordingSigner(), handler, platform: platform);

        var result = await provider.SubmitAtomicTransferGroupAsync(GroupRequest(source: platform.Address.EncodeAsString()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Failed to read atomic transfer group leg");
        handler.BroadcastCount.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_during_group_parameter_lookup_bubbles_to_the_caller()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new CancellingParamsHandler(cancellation);
        var platform = new AlgoAccount();
        var provider = NewProvider(new RecordingSigner(), handler, platform: platform);

        var action = () => provider.SubmitAtomicTransferGroupAsync(
            GroupRequest(source: platform.Address.EncodeAsString()), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.BroadcastCount.Should().Be(0);
    }

    [Fact]
    public async Task One_confirmed_leg_is_nonterminal_until_group_reconciliation()
    {
        var pendingReads = 0;
        using var handler = new AlgodStub(_ =>
            Interlocked.Increment(ref pendingReads) == 1
                ? Confirmed()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var platform = new AlgoAccount();
        var provider = NewProvider(new RecordingSigner(), handler, platform: platform);

        var result = await provider.SubmitAtomicTransferGroupAsync(GroupRequest(source: platform.Address.EncodeAsString()));

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.State.Should().Be(AtomicTransferGroupSubmissionState.PendingConfirmation);
        result.Message.Should().Contain("incomplete confirmation observation");
        handler.BroadcastCount.Should().Be(1);
    }

    private static AlgorandProvider NewProvider(
        ITransactionSigner signer,
        HttpMessageHandler handler,
        IKeyCustodyService? custody = null,
        AlgoAccount? platform = null)
    {
        platform ??= new AlgoAccount();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = "unit-test-wallet-encryption-key-0123456789",
                ["AZOA:Algorand:PlatformMnemonic"] = platform.ToMnemonic(),
            })
            .Build();
        var provider = new AlgorandProvider(
            config,
            NullLogger<AlgorandProvider>.Instance,
            new TransactionSignerFactory(new[] { signer }),
            new WalletKeyService(config),
            custody,
            custodyScopeFactory: null,
            faucet: null,
            httpMessageHandler: handler);
        provider.Initialize(new BlockchainNetworkConfig { NodeUrl = "http://algod.test/" }, ChainNetwork.Devnet);
        return provider;
    }

    private static AtomicTransferGroupRequest GroupRequest(
        string? source = null,
        SigningContext? signingContext = null)
    {
        source ??= new AlgoAccount().Address.EncodeAsString();
        var request = AtomicTransferGroupRequest.TryCreate(
            ProviderBinding(),
            "Algorand",
            ChainNetwork.Devnet,
            "settlement-1",
            new AtomicTransferEffect(
                "12345", source, new AlgoAccount().Address.EncodeAsString(), 90,
                signingContext ?? SigningContext.Platform),
            new AtomicTransferEffect(
                "12345", source, new AlgoAccount().Address.EncodeAsString(), 10,
                signingContext ?? SigningContext.Platform));
        request.IsError.Should().BeFalse(request.Message);
        return request.Result!;
    }

    private static TestProvider ProviderBinding()
    {
        var provider = new TestProvider();
        provider.Initialize(new BlockchainNetworkConfig(), ChainNetwork.Devnet);
        return provider;
    }

    private static HttpResponseMessage Confirmed() => Json(new Dictionary<string, object?>
    {
        ["confirmed-round"] = 12,
        ["pool-error"] = string.Empty,
    });

    private static HttpResponseMessage Params() => Json(new Dictionary<string, object?>
    {
        ["fee"] = 0,
        ["min-fee"] = 1000,
        ["last-round"] = 100,
        ["genesis-id"] = "devnet-v1.0",
        ["genesis-hash"] = "SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=",
    });

    private static HttpResponseMessage Json(object body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private sealed class RecordingSigner : ITransactionSigner
    {
        public List<string> Groups { get; } = [];
        public string ChainType => "Algorand";

        public AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key)
        {
            var transaction = Algorand.Utils.Encoder.DecodeFromMsgPack<Transaction>(canonicalTxn);
            transaction.Group.Should().NotBeNull();
            Groups.Add(transaction.Group!.ToString());
            return new AZOAResult<byte[]> { Result = [0x91] };
        }
    }

    private sealed class CapturingRealSigner : ITransactionSigner
    {
        private readonly AlgorandTransactionSigner _inner = new();

        public List<byte[]> Envelopes { get; } = [];
        public string ChainType => _inner.ChainType;

        public AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key)
        {
            var result = _inner.Sign(canonicalTxn, key);
            if (!result.IsError && result.Result is not null)
                Envelopes.Add(result.Result);
            return result;
        }
    }

    private sealed class AlgodStub : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _pendingResponse;

        public AlgodStub(Func<string, HttpResponseMessage>? pendingResponse = null)
        {
            _pendingResponse = pendingResponse ?? (_ => Confirmed());
        }

        public int RequestCount { get; private set; }
        public int BroadcastCount { get; private set; }
        public byte[] LastBroadcast { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v2/transactions/params", StringComparison.Ordinal))
                return Params();
            if (path == "/v2/transactions")
            {
                BroadcastCount++;
                LastBroadcast = request.Content is null
                    ? []
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                return Json(new Dictionary<string, object?> { ["txId"] = "stub" });
            }
            if (path.Contains("/v2/transactions/pending/", StringComparison.Ordinal))
                return _pendingResponse(path);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class CancellingParamsHandler : HttpMessageHandler
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingParamsHandler(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public int BroadcastCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/v2/transactions/params", StringComparison.Ordinal))
            {
                _cancellation.Cancel();
                throw new OperationCanceledException(_cancellation.Token);
            }

            if (request.RequestUri!.AbsolutePath == "/v2/transactions")
                BroadcastCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class TestProvider : AZOA.WebAPI.Providers.Blockchain.Base.BaseBlockchainProvider
    {
        public TestProvider()
            : base(new ConfigurationBuilder().Build(), NullLogger<TestProvider>.Instance)
        {
        }

        public override string ChainType => "Algorand";
    }
}
