using System.Net;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Signing;

public sealed class AlgorandExchangeWireTests
{
    [Fact]
    public async Task OverlappingPreparersUseTheClaimedGroupEvenAfterNetworkParametersChange()
    {
        using var fixture = new Fixture { HoldFirstSignature = true };
        var first = fixture.Provider.PrepareExchangeAsync(fixture.Intent());
        try
        {
            await fixture.FirstSignatureEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Rpc.Round = 300;
            var second = await fixture.Provider.PrepareExchangeAsync(fixture.Intent()).WaitAsync(TimeSpan.FromSeconds(10));
            second.IsError.Should().BeFalse(second.Message);
            fixture.ReleaseFirstSignature.TrySetResult();
            var initial = await first.WaitAsync(TimeSpan.FromSeconds(10));
            JsonSerializer.Serialize(initial.Result).Should().Be(JsonSerializer.Serialize(second.Result));
            fixture.Store.Rows.Should().ContainSingle();
            fixture.Rpc.Submissions.Should().BeEmpty();
            var before = fixture.Signatures;
            await fixture.Provider.PrepareExchangeAsync(fixture.Intent());
            fixture.Signatures.Should().Be(before);
        }
        finally { fixture.ReleaseFirstSignature.TrySetResult(); }
    }

    [Fact]
    public async Task LostResponseRecoversOnlyTheCompleteFinalizedGroupAndRebroadcastsIdenticalBytes()
    {
        using var fixture = new Fixture();
        var prepared = await fixture.Prepare();
        fixture.Rpc.Prepared = prepared;
        fixture.Rpc.LoseResponse = true;
        await FluentActions.Awaiting(() => fixture.Provider.SubmitExchangeAsync(prepared)).Should().ThrowAsync<HttpRequestException>();
        fixture.Rpc.Partial = true;
        (await fixture.Provider.ObserveExchangeAsync(prepared)).Result!.Verdict.Should().Be("Unknown");
        fixture.Rpc.Partial = false;
        var observed = (await fixture.Provider.ObserveExchangeAsync(prepared)).Result!;
        observed.Verdict.Should().Be("Confirmed");
        observed.GroupId.Should().Be(prepared.GroupId);
        observed.PreparationId.Should().Be(prepared.PreparationId);
        fixture.Rpc.LoseResponse = false;
        await fixture.Provider.SubmitExchangeAsync(prepared);
        fixture.Rpc.Submissions.Should().HaveCount(2);
        fixture.Rpc.Submissions[0].Should().Equal(fixture.Rpc.Submissions[1]);
        fixture.Signatures.Should().Be(3);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("fee")]
    [InlineData("order")]
    [InlineData("missing")]
    public async Task AlteredOrIncompleteFinalizedGroupCannotComplete(string tamper)
    {
        using var fixture = new Fixture();
        var prepared = await fixture.Prepare();
        fixture.Rpc.Prepared = prepared;
        await fixture.Provider.SubmitExchangeAsync(prepared);
        fixture.Rpc.Tamper = tamper;
        (await fixture.Provider.ObserveExchangeAsync(prepared)).Result!.Verdict.Should().Be("Unknown");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task HiddenTrailingEnvelopeCannotPassEvenIfPreparedJournalWasAltered(int index)
    {
        using var fixture = new Fixture();
        var prepared = await fixture.Prepare();
        var items = prepared.SignedTransactionsBase64.ToArray();
        var original = Convert.FromBase64String(items[index]);
        items[index] = Convert.ToBase64String(original.Concat(original).ToArray());
        var altered = prepared with { SignedTransactionsBase64 = items };
        fixture.Store.Rows[prepared.PreparationId].PreparedJson = JsonSerializer.Serialize(altered);
        await FluentActions.Awaiting(() => fixture.Provider.SubmitExchangeAsync(altered)).Should().ThrowAsync<InvalidOperationException>();
        fixture.Rpc.Submissions.Should().BeEmpty();
    }

    [Fact]
    public async Task FullValidityWindowAndCompleteSearchAreRequiredForNoEffectProof()
    {
        using var fixture = new Fixture();
        var prepared = await fixture.Prepare();
        fixture.Rpc.Prepared = prepared;
        (await fixture.Provider.ObserveExchangeAsync(prepared)).Result!.Verdict.Should().Be("Unknown");
        fixture.Rpc.Round = 10000;
        fixture.Rpc.Continuation = "remaining-page";
        (await fixture.Provider.ObserveExchangeAsync(prepared)).Result!.Verdict.Should().Be("Unknown");
        fixture.Rpc.Continuation = null;
        (await fixture.Provider.ObserveExchangeAsync(prepared)).Result!.Verdict.Should().Be("ExpiredAbsent");
        await fixture.Provider.SubmitExchangeAsync(prepared);
        fixture.Rpc.Submissions.Should().BeEmpty();
    }

    [Fact]
    public async Task ScopeAndAcceptedFeeCapsRefuseBeforeCustodyAndNeverMutateAnExistingWire()
    {
        using var fixture = new Fixture();
        var intent = fixture.Intent();
        var legs = intent.Legs.ToArray();
        legs[0] = legs[0] with { SigningContext = legs[0].SigningContext with { Scope = AzoaScopes.NftMint } };
        (await fixture.Provider.PrepareExchangeAsync(intent with { Legs = legs })).IsError.Should().BeTrue();
        (await fixture.Provider.PrepareExchangeAsync(intent with { MaximumNetworkFeeBaseUnits = "2999" })).IsError.Should().BeTrue();
        fixture.Signatures.Should().Be(0);
        fixture.Store.Rows.Should().BeEmpty();
        var prepared = await fixture.Prepare();
        fixture.Rpc.MinimumFee = 5000;
        await fixture.Provider.SubmitExchangeAsync(prepared);
        fixture.Rpc.Submissions.Should().BeEmpty();
        JsonSerializer.Serialize((await fixture.Provider.PrepareExchangeAsync(intent)).Result).Should().Be(JsonSerializer.Serialize(prepared));
        fixture.Signatures.Should().Be(3);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly AlgoAccount _participant = new();
        private readonly AlgoAccount _reserve = new();
        private readonly Guid _actor = Guid.NewGuid();
        private readonly Guid _wallet = Guid.NewGuid();
        private readonly ServiceProvider _services;
        public readonly MemoryPreparationStore Store = new();
        public readonly Rpc Rpc = new();
        public readonly AlgorandProvider Provider;
        public int Signatures;
        public bool HoldFirstSignature;
        public readonly TaskCompletionSource FirstSignatureEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseFirstSignature = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture()
        {
            var custody = new Mock<IKeyCustodyService>(MockBehavior.Strict);
            custody.Setup(service => service.WithSigningKeyAsync(It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
                .Returns((SigningContext context, Func<byte[], Task<AZOAResult<byte[]>>> sign) => Sign(context, sign, false));
            custody.Setup(service => service.WithPlatformSigningKeyAsync(true, It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
                .Returns((bool authorized, SigningContext context, Func<byte[], Task<AZOAResult<byte[]>>> sign) => Sign(context, sign, true));
            _services = new ServiceCollection().AddSingleton<INativePreparationStore>(Store).BuildServiceProvider();
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "devnet", ["Blockchain:Chains:0:ChainType"] = "Algorand",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true", ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://algod.test/",
                ["Blockchain:Chains:0:Devnet:IndexerUrl"] = "http://indexer.test/",
            }).Build();
            Provider = new AlgorandProvider(config, NullLogger<AlgorandProvider>.Instance,
                new TransactionSignerFactory([new AlgorandTransactionSigner()]), keyService: null, custodyService: custody.Object,
                custodyScopeFactory: _services.GetRequiredService<IServiceScopeFactory>(), httpMessageHandler: Rpc);
        }

        private async Task<AZOAResult<AZOAResult<byte[]>>> Sign(SigningContext context, Func<byte[], Task<AZOAResult<byte[]>>> sign, bool platform)
        {
            Store.Rows.Should().ContainSingle("the entire unsigned group must be durable before custody");
            context.IsPlatform.Should().Be(platform);
            if (!platform) { context.UserId.Should().Be(_actor); context.Scope.Should().Be(AzoaScopes.SwapSign); }
            if (Interlocked.Increment(ref Signatures) == 1 && HoldFirstSignature)
            {
                FirstSignatureEntered.TrySetResult();
                await ReleaseFirstSignature.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            return AZOAResult<AZOAResult<byte[]>>.Success(await sign((platform ? _reserve : _participant).KeyPair.ClearTextPrivateKey));
        }

        public ExchangeWireIntent Intent() => new(1, "exchange-fixture", 0, "Algorand", ChainNetwork.Devnet,
            "algorand:" + Convert.ToBase64String(new byte[32]),
            [new("1", "100", _participant.Address.ToString(), _reserve.Address.ToString(), "Participant",
                SigningContext.ForUser(_actor, _wallet).ForChain("Algorand") with { Scope = AzoaScopes.SwapSign }),
             new("2", "50", _reserve.Address.ToString(), _participant.Address.ToString(), "Reserve", SigningContext.PlatformFor("Algorand"))],
            _reserve.Address.ToString(), SigningContext.PlatformFor("Algorand"), "10000");

        public async Task<PreparedExchangeWire> Prepare()
        {
            var result = await Provider.PrepareExchangeAsync(Intent());
            result.IsError.Should().BeFalse(result.Message);
            return result.Result!;
        }
        public void Dispose() { _services.Dispose(); Rpc.Dispose(); }
    }

    private sealed class MemoryPreparationStore : INativePreparationStore
    {
        public Dictionary<string, NativePreparation> Rows { get; } = new();
        public Task<NativePreparation?> GetAsync(string id, CancellationToken ct = default)
        { lock (Rows) return Task.FromResult(Rows.GetValueOrDefault(id)); }
        public Task<NativePreparation> ClaimAsync(NativePreparation candidate, CancellationToken ct = default)
        { lock (Rows) { Rows.TryAdd(candidate.Id, candidate); return Task.FromResult(Rows[candidate.Id]); } }
        public Task<NativePreparation> SavePreparedAsync(string id, string intentHash, string preparedJson, CancellationToken ct = default)
        { lock (Rows) { Rows[id].IntentHash.Should().Be(intentHash); Rows[id].PreparedJson ??= preparedJson; return Task.FromResult(Rows[id]); } }
    }

    private sealed class Rpc : HttpMessageHandler
    {
        public ulong Round = 100;
        public ulong MinimumFee = 1000;
        public bool LoseResponse;
        public bool Partial;
        public string? Continuation;
        public string? Tamper;
        public PreparedExchangeWire? Prepared;
        public readonly List<byte[]> Submissions = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                Submissions.Add(await request.Content!.ReadAsByteArrayAsync(ct));
                if (LoseResponse) throw new HttpRequestException("Lost successful RPC response.");
                return Reply(new { txId = Prepared!.TransactionIds[0] });
            }
            if (path == "/v2/transactions/params") return Reply(new Dictionary<string, object>
            {
                ["fee"] = 0, ["min-fee"] = MinimumFee, ["last-round"] = Round, ["genesis-id"] = "local-v1",
                ["genesis-hash"] = Convert.ToBase64String(new byte[32]), ["consensus-version"] = "test",
            });
            var transactions = Prepared!.SignedTransactionsBase64.Select(item => Encoder.DecodeFromMsgPack<SignedTransaction>(Convert.FromBase64String(item)).Tx).ToArray();
            var receipts = transactions.Select((item, index) => Receipt(item, index)).ToArray();
            if (path.StartsWith("/v2/blocks/", StringComparison.Ordinal))
            {
                var blockTransactions = Tamper == "missing" ? receipts.Take(1).ToArray() : Tamper == "order" ? receipts.Reverse().ToArray() : receipts;
                return Reply(new Dictionary<string, object> { ["round"] = ulong.Parse(path.Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture),
                    ["timestamp"] = 1000, ["genesis-hash"] = Convert.ToBase64String(new byte[32]), ["genesis-id"] = "local-v1",
                    ["transactions"] = blockTransactions });
            }
            path.Should().Be("/v2/transactions");
            var id = Uri.UnescapeDataString(request.RequestUri.Query.Split('=')[1]);
            var found = Submissions.Count > 0 && (!Partial || id == Prepared.TransactionIds[0])
                ? receipts.Where(item => (string)item["id"] == id).ToArray() : [];
            return Reply(new Dictionary<string, object?> { ["current-round"] = Round + 10,
                ["next-token"] = Continuation, ["transactions"] = found });
        }

        private Dictionary<string, object> Receipt(Transaction transaction, int index)
        {
            var result = new Dictionary<string, object> { ["id"] = transaction.TxID(), ["sender"] = transaction.Sender.ToString(),
                ["fee"] = Tamper == "fee" && index == 0 ? 1UL : transaction.Fee!.Value,
                ["first-valid"] = transaction.FirstValid!.Value, ["last-valid"] = transaction.LastValid!.Value,
                ["note"] = Convert.ToBase64String(transaction.Note), ["group"] = Convert.ToBase64String(transaction.Group.Bytes),
                ["confirmed-round"] = transaction.FirstValid.Value + 1, ["intra-round-offset"] = index };
            if (transaction is AssetTransferTransaction asset)
            {
                result["tx-type"] = "axfer";
                result["asset-transfer-transaction"] = new Dictionary<string, object> { ["receiver"] = asset.AssetReceiver.ToString(),
                    ["asset-id"] = asset.XferAsset, ["amount"] = Tamper == "amount" ? 1UL : asset.AssetAmount };
            }
            else
            {
                var payment = (PaymentTransaction)transaction;
                result["tx-type"] = "pay";
                result["payment-transaction"] = new Dictionary<string, object> { ["receiver"] = payment.Receiver.ToString(), ["amount"] = payment.Amount };
            }
            return result;
        }
        private static HttpResponseMessage Reply(object payload) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload)) };
    }
}
