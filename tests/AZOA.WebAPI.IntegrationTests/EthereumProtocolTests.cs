using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.IntegrationTests.Persistence.Surreal;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nethereum.Signer;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests;

public sealed class EthereumProtocolTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private readonly HttpClient _rpc = new() { Timeout = TimeSpan.FromSeconds(30) };
    private Process? _chain;
    private Task? _stderrDrain;
    private long _stderrCharacters;
    private bool _namespaceStarted;
    private ISurrealConnection? _connection;
    private SurrealEthereumPreparationStore _store = null!;
    private IConfiguration _config = null!;
    private LocalCustody _custody = null!;
    private EthECKey[] _keys = [];
    private readonly SigningContext _actor = SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()).ForChain("Ethereum");
    private EthereumProvider NewProvider() => new(_config, NullLogger<EthereumProvider>.Instance,
        new TransactionSignerFactory(new ITransactionSigner[] { new EthereumTransactionSigner() }), _custody, null, null, _store);

    public async Task InitializeAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AZOA.WebAPI.csproj"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Cannot find the repository for the Ethereum protocol fixture.");
        var start = new ProcessStartInfo("node") { WorkingDirectory = root.FullName, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(root.FullName, "tests/AZOA.WebAPI.IntegrationTests/Fixtures/Ethereum/chain.mjs"));
        _chain = Process.Start(start) ?? throw new InvalidOperationException("Cannot start local Ethereum fixture.");
        _stderrDrain = DrainDiagnosticsAsync(_chain.StandardError);
        try
        {
            var handshake = await _chain.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
            if (handshake is null) throw new InvalidOperationException(
                $"Local Ethereum setup failed; build contracts/ethereum prerequisites first. Diagnostic characters received: {Interlocked.Read(ref _stderrCharacters)}.");
            using var document = JsonDocument.Parse(handshake);
            var setup = document.RootElement;
            _rpc.BaseAddress = new Uri(setup.GetProperty("url").GetString()!);
            if (!_rpc.BaseAddress.IsLoopback) throw new InvalidOperationException("Protocol fixture requires loopback RPC.");
            _keys = setup.GetProperty("keys").EnumerateArray().Select(key => new EthECKey(key.GetString()!)).ToArray();
            _custody = new LocalCustody(_keys[0], _keys[1], _actor);
            _config = new ConfigurationBuilder().AddJsonFile(Path.Combine(root.FullName, "appsettings.json"))
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AZOA:Ethereum:AllocatorActivationEpoch"] = _namespace,
                    ["AZOA:Ethereum:PlatformPrivateKey"] = _keys[0].GetPrivateKey(),
                    ["AZOA:Ethereum:RpcRetryMaxAttempts"] = "0",
                    ["AZOA:Ethereum:Nft:Networks:Devnet:ContractAddress"] = setup.GetProperty("address").GetString(),
                    ["AZOA:Ethereum:Nft:Networks:Devnet:RuntimeCodeHash"] = setup.GetProperty("codeHash").GetString(),
                    ["AZOA:Ethereum:Nft:Networks:Devnet:CredentialAuthorityAddress"] = _keys[0].GetPublicAddress(),
                    ["Blockchain:DefaultNetwork"] = "Devnet", ["Blockchain:Chains:0:ChainType"] = "Ethereum",
                    ["Blockchain:Chains:0:Devnet:NodeUrl"] = _rpc.BaseAddress.AbsoluteUri,
                    ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true", ["Blockchain:Chains:0:Devnet:ChainId"] = "31337",
                }).Build();
            _namespaceStarted = true;
            await SurrealTestSchema.BootstrapAsync(_namespace, "ethereum_preparation", "ethereum_signer_checkpoint");
            _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
                Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
            _store = new SurrealEthereumPreparationStore(new DefaultSurrealExecutor(_connection));
            var activation = await NewProvider().ActivateSignerAsync(_keys[1].GetPublicAddress(), _actor.UserId, default);
            activation.IsError.Should().BeFalse(activation.Message);
        }
        catch (Exception failure)
        {
            try { await DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException("Ethereum fixture initialization and cleanup failed.", failure, cleanup); }
            throw;
        }
    }

    [Fact]
    public async Task NativeValue_ReconstructedProviderUsesSameWire_AndReplayDoesNotPayTwice()
    {
        var recipient = _keys[2].GetPublicAddress();
        var before = await BalanceAsync(recipient);
        var intent = new ValueTransferIntent("Ethereum", ChainNetwork.Devnet, "ETH", _keys[1].GetPublicAddress(),
            recipient, "17000", _actor, "local-value:" + Guid.NewGuid().ToString("N"));
        var first = await NewProvider().PrepareValueTransferAsync(intent, default);
        first.IsError.Should().BeFalse(first.Message);
        var recovered = await NewProvider().PrepareValueTransferAsync(intent, default);
        recovered.IsError.Should().BeFalse(recovered.Message);
        recovered.Result.Should().Be(first.Result);
        await NewProvider().ReplayAuthorizedAsync(default);
        (await BalanceAsync(recipient)).Should().Be(before);
        await NewProvider().SubmitValueTransferAsync(recovered.Result!, default);
        await MineAsync();
        (await NewProvider().ObserveValueTransferAsync(recovered.Result!, default)).Result.Should().Be(ChainConfirmation.Confirmed);
        await NewProvider().SubmitValueTransferAsync(recovered.Result!, default);
        (await BalanceAsync(recipient)).Should().Be(before + 17000);
    }

    [Fact]
    public async Task Nft_RealContractVerifiesBothPermits_AndSettlesOwnershipAndFeeOnce()
    {
        var sender = _keys[1].GetPublicAddress();
        var treasury = _keys[3].GetPublicAddress();
        var before = await BalanceAsync(treasury);
        var id = Guid.NewGuid().ToString("N");
        var quote = new NftFeeQuote("Ethereum", ChainNetwork.Devnet, "Mint", 1, "ETH", 18, "19000", treasury, _actor.UserId);
        var authorization = new NftOperationAuthorization(id, "Mint", "Create", 0, _actor.UserId, sender, _actor,
            null, null, null, true, quote);
        var intent = new NftTransactionIntent("Create", "Ethereum", ChainNetwork.Devnet, sender, sender, null,
            "Local protocol fixture", "ipfs://azoa-local-fixture", false, _actor, PreparationKey: id + ":Create:0",
            Fee: new(quote, sender, _actor), Authorization: authorization);
        var first = await NewProvider().PrepareNftTransactionAsync(intent);
        first.IsError.Should().BeFalse(first.Message);
        var recovered = await NewProvider().PrepareNftTransactionAsync(intent);
        recovered.IsError.Should().BeFalse(recovered.Message);
        recovered.Result!.SignedTransactionBase64.Should().Be(first.Result!.SignedTransactionBase64);
        await NewProvider().ReplayAuthorizedAsync(default);
        (await BalanceAsync(treasury)).Should().Be(before);
        await NewProvider().SubmitNftTransactionAsync(recovered.Result);
        await MineAsync();
        var observed = await NewProvider().ObserveNftTransactionAsync(recovered.Result);
        observed.IsError.Should().BeFalse(observed.Message);
        observed.Result!.Status.Should().Be("Confirmed");
        var ownership = await NewProvider().ReadNftOwnershipAsync(observed.Result.AssetId!, sender,
            recovered.Result.ProtocolIdentity, contractAddress: recovered.Result.ContractAddress);
        ownership.IsError.Should().BeFalse(ownership.Message);
        ownership.Result!.Exists.Should().BeTrue();
        EthereumAddress.AreSame(ownership.Result.OwnerAddress, sender).Should().BeTrue();
        await NewProvider().SubmitNftTransactionAsync(recovered.Result);
        (await BalanceAsync(treasury)).Should().Be(before + 19000);
    }

    private async Task<BigInteger> BalanceAsync(string address)
        => BigInteger.Parse("0" + (await CallAsync("eth_getBalance", address, "latest")).GetString()![2..], NumberStyles.HexNumber);

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

    private async Task DrainDiagnosticsAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        int length;
        while ((length = await reader.ReadAsync(buffer.AsMemory())) != 0)
            Interlocked.Add(ref _stderrCharacters, length);
    }

    private async Task StopChainAsync()
    {
        if (_chain is null) return;
        if (!_chain.HasExited)
        {
            _chain.StandardInput.Close();
            try { await _chain.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { _chain.Kill(entireProcessTree: true); await _chain.WaitForExitAsync(); }
        }
        if (_stderrDrain is not null) await _stderrDrain;
        _chain.Dispose();
        _chain = null;
    }

    public async Task DisposeAsync()
    {
        try { await StopChainAsync(); }
        finally
        {
            _rpc.Dispose();
            _connection?.Dispose();
            _connection = null;
            if (_namespaceStarted)
            {
                await SurrealTestSchema.DropAsync(_namespace);
                _namespaceStarted = false;
            }
        }
    }

    private sealed class LocalCustody(EthECKey platform, EthECKey actor, SigningContext actorContext) : IKeyCustodyService
    {
        public Task<AZOAResult<T>> WithSigningKeyAsync<T>(Guid walletId, Guid userId, Func<byte[], Task<T>> sign)
            => WithSigningKeyAsync(SigningContext.ForUser(userId, walletId).ForChain("Ethereum"), sign);
        public Task<AZOAResult<T>> WithSigningKeyAsync<T>(SigningContext context, Func<byte[], Task<T>> sign)
            => context == actorContext ? Sign(actor, sign) : Task.FromResult(AZOAResult<T>.Failure("Unknown local wallet."));
        public Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(bool permitted, Func<byte[], Task<T>> sign)
            => permitted ? Sign(platform, sign) : Task.FromResult(AZOAResult<T>.Failure("Platform signing refused."));
        public Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(bool permitted, SigningContext context, Func<byte[], Task<T>> sign)
            => WithPlatformSigningKeyAsync(context.ChainType!, permitted, context, sign);
        public Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(string chain, bool permitted, SigningContext context, Func<byte[], Task<T>> sign)
            => WithPlatformSigningKeyAsync(permitted && chain == "Ethereum" && context.IsPlatform && !context.IsTenantDriven, sign);
        public Task<AZOAResult<bool>> CanSignAsync(Guid walletId, Guid userId)
            => Task.FromResult(AZOAResult<bool>.Success(walletId == actorContext.WalletId && userId == actorContext.UserId));
        public AZOAResult<IWallet> RewrapAsync(IWallet wallet, WalletKeyService oldKeyService, WalletKeyService newKeyService)
            => AZOAResult<IWallet>.Failure("Local protocol keys are ephemeral.");
        private static async Task<AZOAResult<T>> Sign<T>(EthECKey key, Func<byte[], Task<T>> sign)
        {
            var bytes = key.GetPrivateKeyAsBytes();
            try { return AZOAResult<T>.Success(await sign(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
}
