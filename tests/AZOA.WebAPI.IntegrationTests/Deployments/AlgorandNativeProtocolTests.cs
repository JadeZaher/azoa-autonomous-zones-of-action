using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.IntegrationTests.Persistence.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.IntegrationTests.Deployments;

public sealed class AlgorandNativeProtocolTests
{
    [Fact]
    public async Task RealApplication_CreateCallAndUpdate_ProvesCodeAtCallPositionAndDurableWire()
    {
        await using var rail = new Rail();
        await rail.InitializeAsync();
        var one = await rail.ArtifactAsync("#pragma version 8\nint 1\nreturn\n");
        var two = await rail.ArtifactAsync("#pragma version 8\nint 1\nint 1\n+\nint 2\n==\nreturn\n");
        var create = await rail.QuoteAsync(one);
        var created = await rail.ExecuteAsync(create);
        var application = created.Observation.ResourceId!;
        application.Should().NotBeNullOrWhiteSpace();
        var call = await rail.QuoteAsync(one, application);
        var called = await rail.ExecuteAsync(call);

        var stale = await rail.QuoteAsync(one, application);
        var prepared = await rail.Provider.PrepareNativeStageAsync(stale, "Execute", 0);
        prepared.IsError.Should().BeFalse(prepared.Message);
        await rail.UpdateAsync(application, two);
        var prior = await rail.Provider.ObserveNativeStageAsync(call, called.Prepared);
        prior.Result!.Verdict.Should().Be("Confirmed", "a later update cannot change the programs executed by the older finalized call");

        var sent = await rail.Provider.SubmitNativeStageAsync(stale, prepared.Result!);
        sent.IsError.Should().BeFalse(sent.Message);
        await rail.IndexedAsync(prepared.Result!.TransactionId);
        var changed = await rail.Provider.ObserveNativeStageAsync(stale, prepared.Result);
        changed.IsError.Should().BeFalse(changed.Message);
        changed.Result!.Verdict.Should().NotBe("Confirmed", "a successful call under changed approval code does not prove the selected artifact");

        var current = await rail.QuoteAsync(two, application);
        var currentCall = await rail.ExecuteAsync(current);
        (await rail.Provider.GetNextStageAsync(current, [currentCall])).Result!.Complete.Should().BeTrue();
        (await rail.Provider.GetNextStageAsync(current, [currentCall, currentCall])).IsError.Should().BeTrue();
    }

    private sealed class Rail : IAsyncDisposable
    {
        private readonly string _namespace = "test" + Guid.NewGuid().ToString("N");
        private readonly string _genesis = Environment.GetEnvironmentVariable("AZOA_ALGORAND_LOCAL_GENESIS")
            ?? throw new InvalidOperationException("Pin AZOA_ALGORAND_LOCAL_GENESIS before the local protocol gate.");
        private readonly HttpClient _algod = LocalClient("AZOA_ALGORAND_LOCAL_ALGOD", "http://127.0.0.1:4001/");
        private readonly HttpClient _indexer = LocalClient("AZOA_ALGORAND_LOCAL_INDEXER", "http://127.0.0.1:8980/");
        private readonly SigningContext _owner = SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()).ForChain("Algorand");
        private AlgoAccount _account = null!;
        private LocalNativeCustody _custody = null!;
        private ServiceProvider? _services;
        private ISurrealConnection? _connection;
        private IConfiguration _configuration = null!;
        internal AlgorandProvider Provider { get; private set; } = null!;

        internal async Task InitializeAsync()
        {
            (await GetAsync(_algod, "v2/transactions/params")).GetProperty("genesis-hash").GetString().Should().Be(_genesis);
            var mnemonic = Environment.GetEnvironmentVariable("AZOA_ALGORAND_LOCAL_MNEMONIC")
                ?? throw new InvalidOperationException("Supply a funded account from this local network only.");
            _account = new AlgoAccount(mnemonic);
            _custody = new(_owner, () => (byte[])_account.KeyPair.ClearTextPrivateKey.Clone());
            var balance = await GetAsync(_algod, "v2/accounts/" + _account.Address.EncodeAsString());
            balance.GetProperty("amount").GetUInt64().Should().BeGreaterThan(5_000_000);
            await SurrealTestSchema.BootstrapAsync(_namespace, "native_preparation");
            _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
                Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
            _services = new ServiceCollection().AddSingleton<INativePreparationStore>(new SurrealNativePreparationStore(new DefaultSurrealExecutor(_connection))).BuildServiceProvider();
            _configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "Devnet", ["Blockchain:Chains:0:ChainType"] = "Algorand",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = _algod.BaseAddress!.AbsoluteUri,
                ["Blockchain:Chains:0:Devnet:IndexerUrl"] = _indexer.BaseAddress!.AbsoluteUri,
                ["Blockchain:Chains:0:Devnet:ApiToken"] = Environment.GetEnvironmentVariable("AZOA_ALGORAND_LOCAL_TOKEN") ?? new string('a', 64),
            }).Build();
            Provider = Reconstruct();
        }

        private AlgorandProvider Reconstruct() => new(_configuration, NullLogger<AlgorandProvider>.Instance,
            new TransactionSignerFactory([new AlgorandTransactionSigner()]), keyService: null, custodyService: _custody,
            custodyScopeFactory: _services!.GetRequiredService<IServiceScopeFactory>());

        internal async Task<NativeArtifact> ArtifactAsync(string approval)
        {
            var artifact = new NativeArtifact("avm-application/1", JsonSerializer.SerializeToElement(new AvmApplicationArtifact(
                new("Teal", approval), new("Teal", "#pragma version 8\nint 1\nreturn\n"), new(0, 0), new(0, 0), 0, [], [], [], [], [])), "local-algod");
            var result = await Provider.ValidateArtifactAsync(artifact);
            result.IsError.Should().BeFalse(result.Message);
            return result.Result!;
        }

        internal async Task<NativeOperationIntent> QuoteAsync(NativeArtifact artifact, string? application = null)
        {
            var create = application is null;
            var intent = new NativeOperationIntent(1, Guid.NewGuid().ToString("N"), create ? "Deploy" : "Call", "Algorand", "Devnet", "", CanonicalJson.Hash(artifact),
                JsonSerializer.SerializeToElement(new AvmNativeInput(artifact, _account.Address.EncodeAsString(), create ? "Create" : "Call", application)),
                [new(_account.Address.EncodeAsString(), _owner with { Scope = create ? AzoaScopes.ContractDeploySign : AzoaScopes.ContractCallSign })],
                new("100000", "10000000", "0", ""));
            var signatures = _custody.Signatures;
            var result = await Provider.QuoteNativeOperationAsync(intent);
            result.IsError.Should().BeFalse(result.Message);
            _custody.Signatures.Should().Be(signatures);
            result.Result!.ProtocolIdentity.Should().Be("algorand:" + _genesis);
            intent = intent with { ProtocolIdentity = result.Result.ProtocolIdentity };
            var quote = result.Result with { QuoteId = Guid.NewGuid().ToString("N"), IntentHash = NativeOperationAuthorization.IntentHash(intent) };
            return intent with { Quote = quote, Budget = intent.Budget with { QuoteId = quote.QuoteId } };
        }

        internal async Task<NativeStageHistory> ExecuteAsync(NativeOperationIntent intent)
        {
            var prepared = await Provider.PrepareNativeStageAsync(intent, "Execute", 0);
            prepared.IsError.Should().BeFalse(prepared.Message);
            var signatures = _custody.Signatures;
            var replay = await Reconstruct().PrepareNativeStageAsync(intent, "Execute", 0);
            replay.IsError.Should().BeFalse(replay.Message);
            replay.Result.Should().Be(prepared.Result);
            _custody.Signatures.Should().Be(signatures);
            (await Provider.SubmitNativeStageAsync(intent, prepared.Result!)).IsError.Should().BeFalse();
            await IndexedAsync(prepared.Result!.TransactionId);
            var observed = await Provider.ObserveNativeStageAsync(intent, prepared.Result);
            observed.IsError.Should().BeFalse(observed.Message);
            observed.Result!.Verdict.Should().Be("Confirmed", observed.Result.Detail);
            return new(prepared.Result, observed.Result);
        }

        internal async Task UpdateAsync(string id, NativeArtifact artifact)
        {
            var parameters = await GetAsync(_algod, "v2/transactions/params");
            parameters.GetProperty("genesis-hash").GetString().Should().Be(_genesis);
            var app = artifact.Content.Deserialize<AvmApplicationArtifact>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var transaction = new ApplicationUpdateTransaction
            {
                ApplicationId = ulong.Parse(id, CultureInfo.InvariantCulture), Sender = _account.Address,
                ApprovalProgram = new TEALProgram(Convert.FromBase64String(app.Approval.Data)),
                ClearStateProgram = new TEALProgram(Convert.FromBase64String(app.ClearState.Data)),
                GenesisId = parameters.GetProperty("genesis-id").GetString(), GenesisHash = new Digest(_genesis),
                FirstValid = parameters.GetProperty("last-round").GetUInt64(),
                LastValid = parameters.GetProperty("last-round").GetUInt64() + 1000,
                Fee = Math.Max(1000UL, parameters.GetProperty("min-fee").GetUInt64()),
            };
            using var content = new ByteArrayContent(Encoder.EncodeToMsgPackOrdered(transaction.Sign(_account)));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-binary");
            using var response = await _algod.PostAsync("v2/transactions", content);
            response.EnsureSuccessStatusCode();
            await IndexedAsync(transaction.TxID());
        }

        internal async Task IndexedAsync(string id)
        {
            for (var attempt = 0; attempt < 240; attempt++)
            {
                using var response = await _indexer.GetAsync("v2/transactions/" + Uri.EscapeDataString(id));
                if (response.IsSuccessStatusCode) return;
                if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
                await Task.Delay(250);
            }
            throw new TimeoutException("Local indexer did not observe the finalized transaction.");
        }
        private static HttpClient LocalClient(string variable, string fallback)
        {
            var uri = new Uri(Environment.GetEnvironmentVariable(variable) ?? fallback);
            if (uri.Scheme != "http" || !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address))
                throw new InvalidOperationException("Local AVM fixtures require an HTTP loopback IP.");
            var client = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("X-Algo-API-Token", Environment.GetEnvironmentVariable("AZOA_ALGORAND_LOCAL_TOKEN") ?? new string('a', 64));
            return client;
        }
        private static async Task<JsonElement> GetAsync(HttpClient client, string path)
        {
            using var response = await client.GetAsync(path); response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }
        public async ValueTask DisposeAsync()
        {
            _algod.Dispose(); _indexer.Dispose(); _services?.Dispose(); _connection?.Dispose();
            if (_connection is not null) await SurrealTestSchema.DropAsync(_namespace);
        }
    }
}
