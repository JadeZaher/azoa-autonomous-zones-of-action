using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed partial class TinymanProviderQuoteTests
{
    [Fact]
    public async Task QuoteUsesPinnedPoolCurrentFeesAndFullWidthAssetWithoutSigning()
    {
        using var fixture = new Fixture();
        var result = await fixture.Provider.QuoteSwapAsync(fixture.Terms());
        result.IsError.Should().BeFalse(result.Message);
        result.Result!.InputAsset.Id.Should().Be(ulong.MaxValue.ToString());
        result.Result.ProtocolEvidence.GetProperty("totalFeeShare").GetString().Should().Be("42");
        result.Result.ExpectedOutput.Should().NotBe("0");
        result.Result.Prerequisites.Should().BeEmpty();
        fixture.Rpc.Posts.Should().Be(0);
    }

    [Theory]
    [InlineData("genesis")]
    [InlineData("code")]
    [InlineData("stale")]
    [InlineData("missing-pool")]
    [InlineData("metadata")]
    [InlineData("frozen")]
    [InlineData("unreachable")]
    [InlineData("rekeyed")]
    public async Task UntrustedOrUnavailableProtocolDataProducesDependencyUnavailable(string change)
    {
        using var fixture = new Fixture();
        fixture.Rpc.Change = change;
        var result = await fixture.Provider.QuoteSwapAsync(fixture.Terms());
        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
        result.Result.Should().BeNull();
        fixture.Rpc.Posts.Should().Be(0);
    }

    [Fact]
    public async Task MissingOutputHoldingCreatesImmutableRetainedOptInDisclosure()
    {
        using var fixture = new Fixture();
        fixture.Rpc.Change = "no-holding";
        var terms = fixture.Terms();
        terms = terms with { InputAsset = terms.OutputAsset, OutputAsset = terms.InputAsset };
        var result = await fixture.Provider.QuoteSwapAsync(terms);
        result.IsError.Should().BeFalse(result.Message);
        var effect = result.Result!.Prerequisites.Should().ContainSingle().Which;
        effect.Kind.Should().Be("OptIn");
        effect.AssetId.Should().Be(ulong.MaxValue.ToString());
        effect.Amount.Should().Be("100000");
        effect.RetainedOnSwapFailure.Should().BeTrue();
        (await fixture.Provider.QuoteSwapAsync(terms with
        {
            Budget = terms.Budget with { MaximumLockedBalance = "99999" },
        })).Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task MissingOutputHoldingCannotPromiseFrozenOrUnknownDefaultOptIn(bool? defaultFrozen)
    {
        using var fixture = new Fixture();
        fixture.Rpc.Change = "no-holding";
        fixture.Rpc.DefaultFrozen = defaultFrozen;
        var terms = fixture.Terms();
        var result = await fixture.Provider.QuoteSwapAsync(terms with
        {
            InputAsset = terms.OutputAsset, OutputAsset = terms.InputAsset,
        });
        result.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
        result.Result.Should().BeNull();
        fixture.Rpc.Posts.Should().Be(0);
    }

    [Fact]
    public async Task ExistingUnfrozenOutputHoldingRemainsUsableWithFrozenDefaults()
    {
        using var fixture = new Fixture();
        fixture.Rpc.DefaultFrozen = true;
        var terms = fixture.Terms();
        var result = await fixture.Provider.QuoteSwapAsync(terms with
        {
            InputAsset = terms.OutputAsset, OutputAsset = terms.InputAsset,
        });
        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Prerequisites.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanningAndBrowserValidationBindOriginalQuoteWithoutProviderEgress()
    {
        using var fixture = new Fixture();
        var quote = (await fixture.Provider.QuoteSwapAsync(fixture.Terms())).Result!;
        var intent = new SwapIntent(1, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            CanonicalJson.Hash(quote), quote, new string('a', 64));
        var planned = await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0);
        planned.IsError.Should().BeFalse(planned.Message);
        using var unsigned = JsonDocument.Parse(planned.Result!.UnsignedPayloadJson);
        var envelopes = unsigned.RootElement.GetProperty("transactions").EnumerateArray()
            .Select(value => Encoder.DecodeFromMsgPack<Transaction>(Convert.FromBase64String(value.GetString()!)))
            .Select(transaction => Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(transaction.Sign(RpcHandler.OwnerAccount))))
            .ToArray();
        var validated = fixture.Provider.ValidateSignedSwapStage(intent, planned.Result, JsonSerializer.Serialize(envelopes));
        validated.IsError.Should().BeFalse(validated.Message);
        validated.Result!.TransactionIds.Should().HaveCount(2);
        planned.Result.ReservedNetworkFee.Should().Be("3000");
        fixture.Rpc.Posts.Should().Be(0);
        (fixture.Provider.ValidateSignedSwapStage(intent, planned.Result with { ReservedNetworkFee = "0" },
            JsonSerializer.Serialize(envelopes))).IsError.Should().BeTrue();
        fixture.Rpc.Change = "code";
        (await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 1)).IsError.Should().BeTrue();
    }

    [Theory]
    [InlineData("wallet")]
    [InlineData("chain")]
    public async Task WrongSigningContextIsRefusedBeforeAnyCustodyAccess(string change)
    {
        using var fixture = new Fixture();
        var quote = (await fixture.Provider.QuoteSwapAsync(fixture.Terms())).Result!;
        var intent = new SwapIntent(1, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            CanonicalJson.Hash(quote), quote, new string('a', 64));
        var plan = (await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0)).Result!;
        var context = new SigningContext(quote.ActorUserId, quote.Owner.WalletId, false,
            Scope: AzoaScopes.SwapSign, ChainType: "Algorand");
        context = change == "wallet" ? context with { WalletId = Guid.NewGuid() } : context with { ChainType = "Solana" };
        (await fixture.Provider.SignSwapStageAsync(intent, plan, [new(quote.Owner, context)])).IsError.Should().BeTrue();
        fixture.Custody.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SelfConsistentForgedOptInPlanCannotExceedOriginalRentCap()
    {
        using var fixture = new Fixture();
        fixture.Rpc.Change = "no-holding";
        var requested = fixture.Terms();
        var quote = (await fixture.Provider.QuoteSwapAsync(requested with
        { InputAsset = requested.OutputAsset, OutputAsset = requested.InputAsset })).Result!;
        var intent = new SwapIntent(1, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            CanonicalJson.Hash(quote), quote, new string('a', 64));
        var plan = (await fixture.Provider.PlanSwapStageAsync(intent, "OptIn", 0)).Result!;
        var limited = quote with { Budget = quote.Budget with { MaximumLockedBalance = "99999" } };
        intent = intent with { Terms = limited, QuoteHash = CanonicalJson.Hash(limited) };
        var hash = CanonicalJson.Hash(intent);
        var key = SwapOperationIdentity.Preparation(intent.OperationId, "OptIn", 0, hash);
        var wire = JsonSerializer.Deserialize<TinymanWireParameters>(plan.RecoveryMetadata)! with { PreparationKey = key };
        var transactions = TinymanSwapWire.Build(wire);
        plan = plan with
        {
            IntentHash = hash, PreparationKey = key,
            RecoveryMetadata = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(wire)),
            UnsignedPayloadJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
            {
                format = "algorand-group/1", transactions = transactions.Select(transaction =>
                    Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(transaction))).ToArray(),
            })),
        };
        var envelopes = transactions.Select(transaction =>
            Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(transaction.Sign(RpcHandler.OwnerAccount)))).ToArray();
        fixture.Provider.ValidateSignedSwapStage(intent, plan, JsonSerializer.Serialize(envelopes)).IsError.Should().BeTrue();
        fixture.Custody.VerifyNoOtherCalls();
    }

    private sealed class Fixture : IDisposable
    {
        public readonly RpcHandler Rpc = new();
        public readonly Mock<IKeyCustodyService> Custody = new(MockBehavior.Strict);
        public readonly AlgorandProvider Provider;
        public Fixture()
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 }));
            var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "devnet", ["Blockchain:Chains:0:ChainType"] = "Algorand",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true", ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://algod.test/",
                ["Blockchain:Chains:0:Devnet:IndexerUrl"] = "http://indexer.test/",
                ["Dex:Tinyman:Networks:Devnet:ValidatorAppId"] = "123",
                ["Dex:Tinyman:Networks:Devnet:ApprovalSha256"] = hash,
                ["Dex:Tinyman:Networks:Devnet:ClearStateSha256"] = hash,
                ["Dex:Tinyman:Networks:Devnet:GenesisHash"] = RpcHandler.Genesis,
            }).Build();
            Provider = new(configuration, NullLogger<AlgorandProvider>.Instance, null, null, Custody.Object, null, httpMessageHandler: Rpc);
        }

        public DexQuoteTerms Terms()
        {
            var actor = Guid.NewGuid();
            var owner = new DexParticipant(actor, Guid.NewGuid(), RpcHandler.Owner);
            return new(1, actor, null, owner, owner, owner.Address, new("Algorand", "Devnet", "algorand:" + RpcHandler.Genesis),
                new(ulong.MaxValue.ToString(), "IN", 6, false), new("0", "ALGO", 6, true), "10000", "10000", "0", "0", 100,
                new("input-base-units-v1", 1, "0", 0, "0", ""), new("5000", "100000", "1000"), "", "",
                JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow, []);
        }
        public void Dispose() => Rpc.Dispose();
    }

    private sealed class RpcHandler : HttpMessageHandler
    {
        public static readonly string Genesis = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
        public static readonly global::Algorand.Algod.Model.Account OwnerAccount = new();
        public static readonly string Owner = OwnerAccount.Address.EncodeAsString();
        public string Change = "";
        public bool? DefaultFrozen = false;
        public int Posts;
        public long? LastRound;
        public ulong HoldingAmount = 1000000;
        public string AssetCreator = new global::Algorand.Algod.Model.Account().Address.EncodeAsString();
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Submission;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Indexed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            {
                Posts++;
                return Submission?.Invoke(request, cancellationToken) ?? throw new InvalidOperationException("Quote attempted egress.");
            }
            if (Change == "unreachable") throw new HttpRequestException("fixture outage");
            if (request.RequestUri!.Host == "indexer.test" && Indexed is not null) return Indexed(request, cancellationToken);
            object body;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v2/transactions/params") body = new Dictionary<string, object>
            {
                ["fee"] = 0, ["min-fee"] = 1000, ["last-round"] = LastRound ?? (Change == "stale" ? 999 : 900),
                ["genesis-hash"] = Change == "genesis" ? "other-genesis" : Genesis,
            };
            else if (path == "/v2/applications/123") body = new Dictionary<string, object>
            {
                ["id"] = 123UL, ["params"] = new Dictionary<string, object>
                {
                    ["approval-program"] = Change == "code" ? "BAUG" : "AQID", ["clear-state-program"] = "AQID",
                },
            };
            else if (path.StartsWith("/v2/assets/", StringComparison.Ordinal)) body = new
            {
                index = ulong.MaxValue, @params = new Dictionary<string, object?>
                {
                    ["decimals"] = Change == "metadata" ? 7 : 6, ["unit-name"] = "IN", ["default-frozen"] = DefaultFrozen,
                    ["creator"] = AssetCreator,
                },
            };
            else if (path == "/v2/accounts/" + Owner) body = new JsonObject
            {
                ["address"] = Owner, ["amount"] = 10000000UL, ["min-balance"] = 100000UL,
                ["auth-addr"] = Change == "rekeyed" ? new global::Algorand.Address(Enumerable.Repeat((byte)3, 32).ToArray()).ToString() : Owner,
                ["assets"] = Change == "no-holding" ? new JsonArray() : new JsonArray(new JsonObject
                {
                    ["asset-id"] = ulong.MaxValue, ["amount"] = HoldingAmount, ["is-frozen"] = Change == "frozen",
                }),
            };
            else if (path.StartsWith("/v2/accounts/", StringComparison.Ordinal))
            {
                if (Change == "missing-pool") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                body = TinymanPoolStateTests.Account();
            }
            else throw new InvalidOperationException("Unexpected quote read: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
