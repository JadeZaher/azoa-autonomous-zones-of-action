using System.Net;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
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

public sealed class AlgorandFungibleCreationTests
{
    [Theory]
    [InlineData("18446744073709551615", true)]
    [InlineData("18446744073709551616", false)]
    [InlineData("01", false)]
    [InlineData("0", false)]
    public async Task QuotePreservesSupplyAndHasNoCustodyOrSubmission(string supply, bool accepted)
    {
        using var f = new Fixture();
        var quote = await f.Provider.QuoteFungibleCreationAsync(f.Intent() with { TotalBaseUnits = supply });
        quote.IsError.Should().Be(!accepted);
        if (accepted) { quote.Result!.NetworkFeeBaseUnits.Should().Be("1000"); quote.Result.MinimumBalanceBaseUnits.Should().Be("100000"); }
        f.Signatures.Should().Be(0);
        f.Rpc.Submissions.Should().BeEmpty();
        f.Store.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task PreviouslyAdmittedQuoteCanPrepareAfterExpiryWithOriginalBudget()
    {
        using var f = new Fixture();
        var intent = await f.Quoted();
        var quote = intent.Quote! with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), QuoteId = "" };
        intent = intent with { Quote = quote with { QuoteId = FungibleCreationIdentity.QuoteId(quote) } };
        var result = await f.Provider.PrepareFungibleCreationAsync(intent);
        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Intent.Quote.Should().Be(intent.Quote);
    }

    [Fact]
    public async Task MissingDirectScopeAndNoncanonicalMetadataRefuseBeforeCustody()
    {
        using var f = new Fixture();
        var intent = f.Intent();
        (await f.Provider.QuoteFungibleCreationAsync(intent with { SigningContext = intent.SigningContext with { Scope = null } })).IsError.Should().BeTrue();
        (await f.Provider.QuoteFungibleCreationAsync(intent with { MetadataHashBase64 = " " + Convert.ToBase64String(new byte[32]) })).IsError.Should().BeTrue();
        f.Signatures.Should().Be(0);
        f.Store.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task PreparedRecoveryReusesExactBytesAfterRoundsChangeWithoutSubmitting()
    {
        using var f = new Fixture();
        var intent = await f.Quoted();
        var first = await f.Provider.PrepareFungibleCreationAsync(intent);
        first.IsError.Should().BeFalse(first.Message);
        f.Rpc.Round += 50;
        var second = await f.Provider.PrepareFungibleCreationAsync(intent);
        second.Result.Should().Be(first.Result);
        f.Signatures.Should().Be(1);
        f.Store.Rows.Should().HaveCount(1);
        f.Rpc.Submissions.Should().BeEmpty();
    }

    [Fact]
    public async Task OverlappingPreparersUseOneWinningPlanDespiteDifferentSuggestedRounds()
    {
        using var f = new Fixture();
        f.HoldFirstSignature = true;
        var intent = await f.Quoted();
        var first = f.Provider.PrepareFungibleCreationAsync(intent);
        try
        {
            await f.FirstSignatureEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            f.Rpc.Round += 50;
            var second = await f.Provider.PrepareFungibleCreationAsync(intent);
            f.ReleaseFirstSignature.TrySetResult();
            var original = await first.WaitAsync(TimeSpan.FromSeconds(10));
            original.Result.Should().Be(second.Result);
            f.Store.Rows.Should().ContainSingle();
            f.Rpc.Submissions.Should().BeEmpty();
        }
        finally { f.ReleaseFirstSignature.TrySetResult(); }
    }

    [Fact]
    public async Task LostSendResponseRecoversFinalAssetFromExactReceiptAndRebroadcastsSameWire()
    {
        using var f = new Fixture();
        var prepared = (await f.Provider.PrepareFungibleCreationAsync(await f.Quoted())).Result!;
        f.Rpc.LoseResponse = true;
        await FluentActions.Awaiting(() => f.Provider.SubmitFungibleCreationAsync(prepared)).Should().ThrowAsync<HttpRequestException>();
        var final = await f.Provider.ObserveFungibleCreationAsync(prepared);
        final.IsError.Should().BeFalse(final.Message);
        final.Result!.Verdict.Should().Be("Confirmed");
        final.Result.AssetId.Should().Be("18446744073709551615");
        final.Result.AssetId.Should().NotBe(prepared.TransactionId);
        f.Rpc.LoseResponse = false;
        await f.Provider.SubmitFungibleCreationAsync(prepared);
        f.Rpc.Submissions.Should().HaveCount(2);
        f.Rpc.Submissions[0].Should().Equal(f.Rpc.Submissions[1]);
        f.Signatures.Should().Be(1);
    }

    [Fact]
    public async Task PendingNeverReturnsAnAssetAndPartialHistoryNeverAuthorizesReplacement()
    {
        using var f = new Fixture();
        var prepared = (await f.Provider.PrepareFungibleCreationAsync(await f.Quoted())).Result!;
        var pending = await f.Provider.ObserveFungibleCreationAsync(prepared);
        pending.Result!.Verdict.Should().Be("Pending");
        pending.Result.AssetId.Should().BeNull();
        f.Rpc.Round = 10000;
        f.Rpc.Continuation = "more-history";
        (await f.Provider.ObserveFungibleCreationAsync(prepared)).Result!.Verdict.Should().Be("NeedsOperator");
        f.Rpc.Continuation = null;
        (await f.Provider.ObserveFungibleCreationAsync(prepared)).Result!.Verdict.Should().Be("ExpiredAbsent");
    }

    [Theory]
    [InlineData("total")]
    [InlineData("creator")]
    [InlineData("fee")]
    [InlineData("malformed-fee")]
    public async Task WrongReceiptSemanticsCannotComplete(string tamper)
    {
        using var f = new Fixture();
        var prepared = (await f.Provider.PrepareFungibleCreationAsync(await f.Quoted())).Result!;
        await f.Provider.SubmitFungibleCreationAsync(prepared);
        f.Rpc.Tamper = tamper;
        var result = await f.Provider.ObserveFungibleCreationAsync(prepared);
        result.IsError.Should().BeTrue();
        result.Result.Should().BeNull();
    }

    [Fact]
    public async Task WrongCustodyKeyAndTrailingEnvelopeCannotBecomeASavedCandidate()
    {
        using var f = new Fixture();
        var intent = await f.Quoted();
        f.WrongSigningKey = true;
        await FluentActions.Awaiting(() => f.Provider.PrepareFungibleCreationAsync(intent)).Should().ThrowAsync<InvalidOperationException>();
        f.Store.Rows.Values.Single().PreparedJson.Should().BeNull();
        f.WrongSigningKey = false;
        var prepared = (await f.Provider.PrepareFungibleCreationAsync(intent)).Result!;
        var wire = Convert.FromBase64String(prepared.SignedTransactionBase64);
        var appended = prepared with { SignedTransactionBase64 = Convert.ToBase64String(wire.Concat(wire).ToArray()) };
        f.Store.Rows[prepared.PreparationId].PreparedJson = JsonSerializer.Serialize(appended);
        await FluentActions.Awaiting(() => f.Provider.SubmitFungibleCreationAsync(appended)).Should().ThrowAsync<InvalidOperationException>();
        f.Rpc.Submissions.Should().BeEmpty();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly AlgoAccount _account = new();
        private readonly ServiceProvider _services;
        private readonly Guid _actor = Guid.NewGuid();
        private readonly Guid _wallet = Guid.NewGuid();
        public readonly MemoryPreparationStore Store = new();
        public readonly Rpc Rpc = new();
        public readonly AlgorandProvider Provider;
        public int Signatures;
        public bool WrongSigningKey;
        public bool HoldFirstSignature;
        public readonly TaskCompletionSource FirstSignatureEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseFirstSignature = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture()
        {
            var custody = new Mock<IKeyCustodyService>(MockBehavior.Strict);
            custody.Setup(service => service.WithSigningKeyAsync(It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
                .Returns(async (SigningContext context, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
                {
                    Store.Rows.Should().ContainSingle("the unsigned plan must win before custody");
                    context.UserId.Should().Be(_actor);
                    if (Interlocked.Increment(ref Signatures) == 1 && HoldFirstSignature)
                    {
                        FirstSignatureEntered.TrySetResult();
                        await ReleaseFirstSignature.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    var account = WrongSigningKey ? new AlgoAccount() : _account;
                    return AZOAResult<AZOAResult<byte[]>>.Success(await sign(account.KeyPair.ClearTextPrivateKey));
                });
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

        public FungibleCreationIntent Intent() => new("fixture-create", 0, _actor, null, "Algorand", ChainNetwork.Devnet,
            _account.Address.EncodeAsString(), SigningContext.ForUser(_actor, _wallet) with { Scope = AzoaScopes.TokenCreateSign }, "Asset", "AST", "18446744073709551615", 19,
            false, "", "", "", "");
        public async Task<FungibleCreationIntent> Quoted()
        {
            var intent = Intent();
            return intent with { Quote = (await Provider.QuoteFungibleCreationAsync(intent)).Result! };
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
        public bool LoseResponse;
        public string? Continuation;
        public string? Tamper;
        public readonly List<byte[]> Submissions = [];
        private AssetCreateTransaction? _confirmed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                var wire = await request.Content!.ReadAsByteArrayAsync(ct);
                Submissions.Add(wire);
                _confirmed = (AssetCreateTransaction)Encoder.DecodeFromMsgPack<SignedTransaction>(wire).Tx;
                if (LoseResponse) throw new HttpRequestException("Lost successful RPC response.");
                return Reply(new { txId = _confirmed.TxID() });
            }
            if (path == "/v2/transactions/params") return Reply(new Dictionary<string, object>
            {
                ["fee"] = 0, ["min-fee"] = 1000, ["last-round"] = Round, ["genesis-id"] = "local-v1",
                ["genesis-hash"] = Convert.ToBase64String(new byte[32]), ["consensus-version"] = "test",
            });
            if (path.StartsWith("/v2/blocks/", StringComparison.Ordinal))
                return Reply(new Dictionary<string, object> { ["genesis-hash"] = Convert.ToBase64String(new byte[32]) });
            path.Should().Be("/v2/transactions");
            return Reply(new Dictionary<string, object?> { ["current-round"] = Round + 10,
                ["next-token"] = Continuation, ["transactions"] = _confirmed is null ? Array.Empty<object>() : [Receipt(_confirmed)] });
        }
        private Dictionary<string, object> Receipt(AssetCreateTransaction tx) => new()
        {
            ["id"] = tx.TxID(), ["tx-type"] = "acfg", ["sender"] = tx.Sender.EncodeAsString(),
            ["fee"] = Tamper == "malformed-fee" ? "not-a-number" : Tamper == "fee" ? 999UL : tx.Fee!.Value,
            ["first-valid"] = tx.FirstValid!.Value, ["last-valid"] = tx.LastValid!.Value,
            ["note"] = Convert.ToBase64String(tx.Note), ["confirmed-round"] = tx.FirstValid.Value + 1,
            ["created-asset-index"] = ulong.MaxValue, ["asset-config-transaction"] = new Dictionary<string, object>
            {
                ["asset-id"] = 0, ["params"] = new Dictionary<string, object>
                {
                    ["creator"] = Tamper == "creator" ? new AlgoAccount().Address.EncodeAsString() : tx.Sender.EncodeAsString(),
                    ["total"] = Tamper == "total" ? 1UL : ulong.MaxValue, ["decimals"] = 19,
                    ["name"] = "Asset", ["unit-name"] = "AST",
                },
            },
        };
        private static HttpResponseMessage Reply(object payload) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload)) };
    }
}
