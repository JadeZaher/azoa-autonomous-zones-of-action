using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.IntegrationTests.Persistence.Surreal;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nethereum.Signer;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests;

/// <summary>Actual pinned Uniswap V3 + AzoaDexExecutor settlement through the production Ethereum DEX module and durable stores.</summary>
public sealed class EthereumDexProtocolTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private readonly HttpClient _rpc = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _wallet = Guid.NewGuid();
    private Process? _chain;
    private Task? _stderrDrain;
    private bool _namespaceStarted;
    private ISurrealConnection? _connection;
    private ISurrealExecutor _executor = null!;
    private ServiceProvider? _services;
    private IConfiguration _config = null!;
    private JsonElement _setup;
    private EthECKey[] _keys = [];

    private EthereumProvider NewProvider() => new(_config, NullLogger<EthereumProvider>.Instance,
        new TransactionSignerFactory(new ITransactionSigner[] { new EthereumTransactionSigner() }), new OwnerCustody(_keys[1], _actor, _wallet),
        _services!.GetRequiredService<IServiceScopeFactory>(), null, _services!.GetRequiredService<IEthereumPreparationStore>());

    public async Task InitializeAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AZOA.WebAPI.csproj"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Cannot find the repository for the Ethereum DEX protocol fixture.");
        var start = new ProcessStartInfo("node") { WorkingDirectory = root.FullName, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(root.FullName, "tests/AZOA.WebAPI.IntegrationTests/Fixtures/Ethereum/dex-chain.mjs"));
        _chain = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the local Ethereum DEX fixture.");
        _stderrDrain = DrainDiagnosticsAsync(_chain.StandardError);
        try
        {
            var handshake = await _chain.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120))
                ?? throw new InvalidOperationException("Local DEX chain setup failed; build contracts/ethereum artifacts and install its pinned dependencies first.");
            using (var document = JsonDocument.Parse(handshake)) _setup = document.RootElement.Clone();
            _rpc.BaseAddress = new Uri(Text("url"));
            if (!_rpc.BaseAddress.IsLoopback) throw new InvalidOperationException("Protocol fixture requires loopback RPC.");
            _keys = _setup.GetProperty("keys").EnumerateArray().Select(key => new EthECKey(key.GetString()!)).ToArray();
            var prefix = "AZOA:Ethereum:Dex:Networks:Devnet:";
            var settings = new Dictionary<string, string?>
            {
                ["AZOA:Ethereum:AllocatorActivationEpoch"] = _namespace, ["AZOA:Ethereum:RpcRetryMaxAttempts"] = "0",
                ["Blockchain:DefaultNetwork"] = "Devnet", ["Blockchain:Chains:0:ChainType"] = "Ethereum",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = _rpc.BaseAddress.AbsoluteUri,
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true", ["Blockchain:Chains:0:Devnet:ChainId"] = "31337",
                [prefix + "Enabled"] = "true", [prefix + "ArchiveStateConfirmed"] = "true", [prefix + "PoolFee"] = "3000", [prefix + "ExecutionLifetimeSeconds"] = "3600",
                [prefix + "MaximumFinalizedLagSeconds"] = "7200", [prefix + "MaximumFutureSkewSeconds"] = "120",
                [prefix + "Executor"] = Text("executor"), [prefix + "Router"] = Text("router"), [prefix + "Factory"] = Text("factory"),
                [prefix + "Quoter"] = Text("quoter"), [prefix + "WrappedNative"] = Text("weth"), [prefix + "PolicySigner"] = _keys[4].GetPublicAddress(),
                [prefix + "PolicyKeyVersion"] = Text("policyVersion"), [prefix + "GenesisHash"] = Text("genesisHash"),
                [prefix + "ExecutorCodeHash"] = Text("executorHash"), [prefix + "RouterCodeHash"] = Text("routerHash"),
                [prefix + "FactoryCodeHash"] = Text("factoryHash"), [prefix + "QuoterCodeHash"] = Text("quoterHash"),
                [prefix + "WrappedNativeCodeHash"] = Text("wethHash"), [prefix + "WrappedNativeSourceRevision"] = "WETH9-fixture",
                [prefix + "ResetGasLimit"] = "100000", [prefix + "ApproveGasLimit"] = "100000", [prefix + "SwapGasLimit"] = "600000",
                [prefix + "CleanupGasLimit"] = "100000",
                ["AZOA:Ethereum:Dex:PolicyKeys:" + Text("policyVersion")[2..].ToLowerInvariant() + ":PrivateKey"] = "0x" + Convert.ToHexString(_keys[4].GetPrivateKeyAsBytes()),
            };
            foreach (var (token, hash) in new[] { ("input", "inputHash"), ("output", "outputHash") })
            {
                var tokenPrefix = prefix + "Tokens:" + Text(token).ToLowerInvariant() + ":";
                settings[tokenPrefix + "CodeHash"] = Text(hash);
                settings[tokenPrefix + "SourceRevision"] = "DexTestToken-fixture";
                settings[tokenPrefix + "Standard"] = "ERC20Immutable";
            }
            _config = new ConfigurationBuilder().AddJsonFile(Path.Combine(root.FullName, "appsettings.json")).AddInMemoryCollection(settings).Build();
            _namespaceStarted = true;
            await SurrealTestSchema.BootstrapAsync(_namespace, "ethereum_preparation", "ethereum_signer_checkpoint", "ethereum_dex_allowance",
                "swap_operation", "swap_stage");
            _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
                Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
            _executor = new DefaultSurrealExecutor(_connection);
            _services = new ServiceCollection()
                .AddSingleton<IEthereumPreparationStore>(new SurrealEthereumPreparationStore(_executor))
                .AddSingleton<IEthereumDexAllowanceStore>(new SurrealEthereumDexAllowanceStore(_executor))
                .BuildServiceProvider();
            await MineAsync();
            var activation = await NewProvider().ActivateSignerAsync(_keys[1].GetPublicAddress(), _actor, default);
            activation.IsError.Should().BeFalse(activation.Message);
        }
        catch (Exception failure)
        {
            try { await DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException("Ethereum DEX fixture initialization and cleanup failed.", failure, cleanup); }
            throw;
        }
    }

    [Fact]
    public async Task Erc20_swap_settles_through_exact_approval_and_releases_the_allowance_only_on_finalized_proof()
    {
        var owner = new DexParticipant(_actor, _wallet, _keys[1].GetPublicAddress());
        var treasury = _keys[3].GetPublicAddress();
        var gross = BigInteger.Pow(10, 17); var fee = BigInteger.Pow(10, 14);
        var requested = new DexQuoteTerms(1, _actor, null, owner, owner, owner.Address, new("Ethereum", "Devnet", "eip155:31337"),
            new(Text("input"), "IN", 18, false), new(Text("output"), "OUT", 18, false), gross.ToString(CultureInfo.InvariantCulture),
            (gross - fee).ToString(CultureInfo.InvariantCulture), "0", "0", 50,
            new(NodeFeeSchedule.InputBaseUnitsV1, 1, "0", 10, fee.ToString(CultureInfo.InvariantCulture), treasury, Text("policyVersion")[2..].ToLowerInvariant()),
            new("100000000000000000", "0", "10000000000000000"), "UniswapV3", "classic-exact-input/1",
            JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow, []);
        var quoted = await NewProvider().QuoteSwapAsync(requested);
        quoted.IsError.Should().BeFalse(quoted.Message);
        var terms = quoted.Result!;
        terms.Prerequisites.Select(effect => effect.Kind).Should().Equal("ApproveExact");
        var intent = new SwapIntent(1, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), Guid.NewGuid().ToString("N"),
            CanonicalJson.Hash(terms), terms, new string('f', 64));
        await Save(new SwapOperationRecord { Id = intent.OperationId, IntentHash = CanonicalJson.Hash(intent), State = "Admitted",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow });
        var ownerInputBefore = await TokenBalanceAsync("input", owner.Address);
        var treasuryBefore = await TokenBalanceAsync("input", treasury);
        var ownerOutputBefore = await TokenBalanceAsync("output", owner.Address);

        var approval = await SettleAsync(intent, "ApproveExact");
        approval.Observation.Verdict.Should().Be("Confirmed");
        (await NewProvider().GetNextSwapStageAsync(intent, [new(approval.Prepared, approval.Observation)])).Result!.Stage.Should().Be("Swap");
        var swap = await SettleAsync(intent, "Swap");

        swap.Observation.Verdict.Should().Be("Confirmed");
        swap.Observation.ActualInput.Should().Be(gross.ToString(CultureInfo.InvariantCulture));
        swap.Observation.PlatformFeePaid.Should().Be(fee.ToString(CultureInfo.InvariantCulture));
        var output = BigInteger.Parse(swap.Observation.ActualOutput!, CultureInfo.InvariantCulture);
        (output >= BigInteger.Parse(terms.MinimumOutput, CultureInfo.InvariantCulture)).Should().BeTrue();
        (ownerInputBefore - await TokenBalanceAsync("input", owner.Address)).Should().Be(gross);
        (await TokenBalanceAsync("input", treasury) - treasuryBefore).Should().Be(fee);
        (await TokenBalanceAsync("output", owner.Address) - ownerOutputBefore).Should().Be(output);
        (await NewProvider().GetNextSwapStageAsync(intent, [new(approval.Prepared, approval.Observation), new(swap.Prepared, swap.Observation)]))
            .Result!.Complete.Should().BeTrue();

        (await NewProvider().SubmitSwapStageAsync(intent, swap.Prepared)).IsError.Should().BeFalse("replay after finality is a no-send success");
        (ownerInputBefore - await TokenBalanceAsync("input", owner.Address)).Should().Be(gross, "the saved wire never executes twice");
        var stageId = "settled-" + intent.OperationId[..16];
        await Save(new SwapStageRecord { Id = stageId, OperationId = intent.OperationId, IntentHash = CanonicalJson.Hash(intent), Stage = "Swap",
            State = "Confirmed", PreparedJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(swap.Prepared)), AuthorizedAt = DateTimeOffset.UtcNow,
            ObservationJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(swap.Observation)),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        (await NewProvider().ReleaseRetainedResourcesAsync(intent, stageId, swap.Prepared, swap.Observation)).Should().BeTrue();
        var allowance = (await _executor.QueryAsync<EthereumDexAllowance>(SurrealForge.Client.Query.SurrealQuery<EthereumDexAllowance>.From())).Single();
        (allowance.State, allowance.ReleaseStageId).Should().Be(("Released", stageId));
    }

    private sealed record SettledStage(PreparedSwapStage Prepared, SwapStageObservation Observation);

    private async Task<SettledStage> SettleAsync(SwapIntent intent, string stage)
    {
        var planned = await NewProvider().PlanSwapStageAsync(intent, stage, 0);
        planned.IsError.Should().BeFalse(planned.Message);
        var context = new SigningContext(_actor, _wallet, false, Guid.Empty, _actor, AzoaScopes.SwapSign, "Ethereum");
        var signed = await NewProvider().SignSwapStageAsync(intent, planned.Result!, [new(intent.Terms.Owner, context)]);
        signed.IsError.Should().BeFalse(signed.Message);
        (await NewProvider().SubmitSwapStageAsync(intent, signed.Result!)).IsError.Should().BeFalse();
        await MineAsync();
        var observed = await NewProvider().ObserveSwapStageAsync(intent, signed.Result!);
        observed.IsError.Should().BeFalse(observed.Message);
        var repeated = await NewProvider().ObserveSwapStageAsync(intent, signed.Result!);
        repeated.Result.Should().BeEquivalentTo(observed.Result, "a reconstructed provider reaches the identical finalized verdict");
        return new(signed.Result!, observed.Result!);
    }

    private async Task<BigInteger> TokenBalanceAsync(string token, string holder)
    {
        var result = await CallAsync("eth_call", new { to = Text(token), data = EthereumAbi.EncodeCall("balanceOf(address)", holder) }, "latest");
        return BigInteger.Parse("0" + result.GetString()![2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private async Task MineAsync()
    {
        for (var i = 0; i < 40; i++) await CallAsync("evm_mine");
    }

    private async Task<JsonElement> CallAsync(string method, params object[] args)
    {
        using var response = await _rpc.PostAsJsonAsync("", new { jsonrpc = "2.0", id = 1, method, @params = args });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (document.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("Local Ethereum rejected " + method);
        return document.RootElement.GetProperty("result").Clone();
    }

    private async Task Save<T>(T row) where T : class, ISurrealRecord, new()
        => (await _executor.ExecuteAsync(SurrealWriter.Create(row))).EnsureAllOk();

    private string Text(string name) => _setup.GetProperty(name).GetString()!;

    /// <summary>Consumes child diagnostics through a fixed buffer without printing or retaining them.</summary>
    private static async Task DrainDiagnosticsAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory()) != 0) { }
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_chain is not null && !_chain.HasExited)
            {
                _chain.StandardInput.Close();
                try { await _chain.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { _chain.Kill(entireProcessTree: true); await _chain.WaitForExitAsync(); }
            }
            if (_stderrDrain is not null) await _stderrDrain;
            _chain?.Dispose();
            _chain = null;
        }
        finally
        {
            _rpc.Dispose();
            _services?.Dispose();
            _connection?.Dispose();
            _connection = null;
            if (_namespaceStarted)
            {
                await SurrealTestSchema.DropAsync(_namespace);
                _namespaceStarted = false;
            }
        }
    }

    private sealed class OwnerCustody(EthECKey owner, Guid actor, Guid wallet) : IKeyCustodyService
    {
        public Task<AZOAResult<T>> WithSigningKeyAsync<T>(Guid walletId, Guid userId, Func<byte[], Task<T>> sign)
            => WithSigningKeyAsync(SigningContext.ForUser(userId, walletId).ForChain("Ethereum"), sign);
        public Task<AZOAResult<T>> WithSigningKeyAsync<T>(SigningContext context, Func<byte[], Task<T>> sign)
            => !context.IsPlatform && context.UserId == actor && context.WalletId == wallet ? Sign(sign)
                : Task.FromResult(AZOAResult<T>.Failure("Unknown local wallet."));
        public Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(bool permitted, Func<byte[], Task<T>> sign)
            => Task.FromResult(AZOAResult<T>.Failure("The DEX fixture never uses platform custody."));
        public Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(bool permitted, SigningContext context, Func<byte[], Task<T>> sign)
            => WithPlatformSigningKeyAsync(permitted, sign);
        public Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(string chain, bool permitted, SigningContext context, Func<byte[], Task<T>> sign)
            => WithPlatformSigningKeyAsync(permitted, sign);
        public Task<AZOAResult<bool>> CanSignAsync(Guid walletId, Guid userId)
            => Task.FromResult(AZOAResult<bool>.Success(walletId == wallet && userId == actor));
        public AZOAResult<IWallet> RewrapAsync(IWallet wallet, WalletKeyService oldKeyService, WalletKeyService newKeyService)
            => AZOAResult<IWallet>.Failure("Local protocol keys are ephemeral.");
        private async Task<AZOAResult<T>> Sign<T>(Func<byte[], Task<T>> sign)
        {
            var bytes = owner.GetPrivateKeyAsBytes();
            try { return AZOAResult<T>.Success(await sign(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
}
