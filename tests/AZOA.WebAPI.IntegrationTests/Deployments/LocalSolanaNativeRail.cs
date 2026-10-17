using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.IntegrationTests.Persistence.Surreal;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Solnet.Wallet;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Deployments;

internal sealed class LocalSolanaNativeRail : IAsyncDisposable
{
    private readonly string _namespace = "test" + Guid.NewGuid().ToString("N");
    private readonly HttpClient _rpc;
    private readonly string _expectedGenesis;
    private ISurrealConnection? _connection;
    private ServiceProvider? _services;
    private IConfiguration? _configuration;
    internal Account Actor { get; } = new();
    internal SigningContext Context { get; } = SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()).ForChain("Solana");
    internal LocalNativeCustody Custody { get; }
    internal SolanaProvider Provider { get; private set; } = null!;

    internal LocalSolanaNativeRail()
    {
        var uri = new Uri(Environment.GetEnvironmentVariable("AZOA_SOLANA_VALIDATOR_URL") ?? "http://127.0.0.1:18899");
        if (uri.Scheme != "http" || !IPAddress.TryParse(uri.Host, out var host) || !IPAddress.IsLoopback(host))
            throw new InvalidOperationException("Native protocol fixtures require an HTTP loopback IP.");
        _expectedGenesis = Environment.GetEnvironmentVariable("AZOA_SOLANA_LOCAL_GENESIS")
            ?? throw new InvalidOperationException("Pin AZOA_SOLANA_LOCAL_GENESIS from the local validator before running the fixture.");
        _rpc = new() { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
        Custody = new(Context, () => (byte[])Actor.PrivateKey.KeyBytes.Clone());
    }

    internal async Task InitializeAsync()
    {
        (await RpcAsync("getGenesisHash")).GetString().Should().Be(_expectedGenesis);
        await SurrealTestSchema.BootstrapAsync(_namespace, "native_preparation");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _services = new ServiceCollection().AddSingleton<INativePreparationStore>(new SurrealNativePreparationStore(new DefaultSurrealExecutor(_connection)))
            .BuildServiceProvider();
        _configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AZOA:Solana:Networks:Devnet:GenesisHash"] = _expectedGenesis,
            ["Blockchain:DefaultNetwork"] = "Devnet", ["Blockchain:Chains:0:ChainType"] = "Solana",
            ["Blockchain:Chains:0:Devnet:NodeUrl"] = _rpc.BaseAddress!.AbsoluteUri,
        }).Build();
        Provider = Reconstruct();
        var signature = (await RpcAsync("requestAirdrop", Actor.PublicKey.Key, 20_000_000_000UL)).GetString()!;
        await FinalizedAsync(signature);
    }

    internal SolanaProvider Reconstruct() => new(_configuration!, NullLogger<SolanaProvider>.Instance,
        new TransactionSignerFactory([new SolanaTransactionSigner()]), Custody,
        _services!.GetRequiredService<IServiceScopeFactory>(), null);

    internal async Task<NativeOperationIntent> QuoteAsync(NativeArtifact artifact, string operation = "Create",
        string? program = null, bool immutable = false, string? expectedProgramHash = null, uint capacity = 0)
    {
        var validated = await Provider.ValidateArtifactAsync(artifact);
        validated.IsError.Should().BeFalse(validated.Message);
        var kind = operation == "Call" ? "Call" : artifact.Format == "solana-account/1" ? "Account" : "Deploy";
        var scope = kind == "Call" ? AzoaScopes.ContractCallSign : kind == "Account" ? AzoaScopes.SolanaAccountSign : AzoaScopes.ContractDeploySign;
        var intent = new NativeOperationIntent(1, Guid.NewGuid().ToString("N"), kind, "Solana", "Devnet", "", CanonicalJson.Hash(validated.Result!),
            JsonSerializer.SerializeToElement(new SolanaNativeInput(validated.Result!, Actor.PublicKey.Key, Actor.PublicKey.Key,
                operation, program, immutable, capacity, Actor.PublicKey.Key, expectedProgramHash)),
            [new(Actor.PublicKey.Key, Context with { Scope = scope })], new("100000000", "10000000000", "0", ""));
        var signatures = Custody.Signatures;
        var quote = await Provider.QuoteNativeOperationAsync(intent);
        quote.IsError.Should().BeFalse(quote.Message);
        Custody.Signatures.Should().Be(signatures);
        intent = intent with { ProtocolIdentity = quote.Result!.ProtocolIdentity };
        var accepted = quote.Result with { QuoteId = Guid.NewGuid().ToString("N"), IntentHash = NativeOperationAuthorization.IntentHash(intent) };
        return intent with { Quote = accepted, Budget = intent.Budget with { QuoteId = accepted.QuoteId } };
    }

    internal async Task<(NativeNextStage Completed, List<NativeStageHistory> History)> ExecuteAsync(NativeOperationIntent intent,
        Func<string, Task>? beforeStage = null)
    {
        var history = new List<NativeStageHistory>();
        for (var index = 0; index < 512; index++)
        {
            var next = await Provider.GetNextStageAsync(intent, history);
            next.IsError.Should().BeFalse(next.Message);
            if (next.Result!.Complete) return (next.Result, history);
            if (beforeStage is not null) await beforeStage(next.Result.Stage!);
            var prepared = await Provider.PrepareNativeStageAsync(intent, next.Result.Stage!, 0);
            prepared.IsError.Should().BeFalse(prepared.Message);
            var signatures = Custody.Signatures;
            var replay = await Reconstruct().PrepareNativeStageAsync(intent, next.Result.Stage!, 0);
            replay.IsError.Should().BeFalse(replay.Message);
            replay.Result.Should().Be(prepared.Result);
            Custody.Signatures.Should().Be(signatures);
            var sent = await Provider.SubmitNativeStageAsync(intent, prepared.Result!);
            sent.IsError.Should().BeFalse(sent.Message);
            await FinalizedAsync(prepared.Result!.TransactionId);
            var observed = await Provider.ObserveNativeStageAsync(intent, prepared.Result);
            observed.IsError.Should().BeFalse(observed.Message);
            observed.Result!.Verdict.Should().Be("Confirmed", observed.Result.Detail);
            history.Add(new(prepared.Result, observed.Result));
        }
        throw new InvalidOperationException("Local native stage plan exceeded its bounded fixture limit.");
    }

    internal async Task<JsonElement> RpcAsync(string method, params object[] args)
    {
        using var response = await _rpc.PostAsJsonAsync("", new { jsonrpc = "2.0", id = 1, method, @params = args });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (document.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("Local RPC rejected " + method);
        return document.RootElement.GetProperty("result").Clone();
    }

    internal async Task FinalizedAsync(string signature)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            var result = (await RpcAsync("getSignatureStatuses", new[] { signature }, new { searchTransactionHistory = true })).GetProperty("value")[0];
            if (result.ValueKind != JsonValueKind.Null && result.GetProperty("confirmationStatus").GetString() == "finalized")
            { result.GetProperty("err").ValueKind.Should().Be(JsonValueKind.Null); return; }
            await Task.Delay(250);
        }
        throw new TimeoutException("Local native transaction did not finalize.");
    }

    internal async Task SendSetupAsync(params TransactionInstruction[] instructions)
    {
        (await RpcAsync("getGenesisHash")).GetString().Should().Be(_expectedGenesis);
        var blockhash = (await RpcAsync("getLatestBlockhash", new { commitment = "finalized" })).GetProperty("value").GetProperty("blockhash").GetString()!;
        var transaction = new TransactionBuilder().SetRecentBlockHash(blockhash).SetFeePayer(Actor);
        foreach (var instruction in instructions) transaction.AddInstruction(instruction);
        var wire = transaction.Build(Actor);
        var signature = (await RpcAsync("sendTransaction", Convert.ToBase64String(wire), new { encoding = "base64", preflightCommitment = "finalized" })).GetString()!;
        await FinalizedAsync(signature);
    }

    public async ValueTask DisposeAsync()
    {
        _rpc.Dispose(); _services?.Dispose(); _connection?.Dispose();
        if (_connection is not null) await SurrealTestSchema.DropAsync(_namespace);
    }
}
