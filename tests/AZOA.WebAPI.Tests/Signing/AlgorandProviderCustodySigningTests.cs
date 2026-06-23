using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using Xunit;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// value-path-wiring C1: a per-user custodial value-move signs with the USER's
/// key (resolved via <see cref="IKeyCustodyService.WithSigningKeyAsync{T}"/> with
/// the user's walletId/avatarId), NOT the platform key — and a non-owning avatar
/// is IDOR-rejected by the custody guard with NO signing side effect.
/// </summary>
public class AlgorandProviderCustodySigningTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly AlgoAccount _userAccount = new();

    private int _submitCount;

    public AlgorandProviderCustodySigningTests()
    {
        var port = GetFreePort();
        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
    }

    private IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AZOA:WalletEncryptionKey"] = "unit-test-wallet-encryption-key-0123456789",
            // A platform mnemonic IS configured — the test proves the per-user path
            // does NOT use it (it routes through the custody resolver instead).
            ["AZOA:Algorand:PlatformMnemonic"] = new AlgoAccount().ToMnemonic(),
            ["Blockchain:DefaultNetwork"] = "devnet",
            ["Blockchain:Chains:0:ChainType"] = "Algorand",
            ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
            ["Blockchain:Chains:0:Devnet:NodeUrl"] = _baseUrl,
            ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "5000",
        })
        .Build();

    private AlgorandProvider NewProvider(IKeyCustodyService custody)
    {
        var config = BuildConfig();
        var signerFactory = new TransactionSignerFactory(new[] { new AlgorandTransactionSigner() });
        return new AlgorandProvider(
            config, NullLogger<AlgorandProvider>.Instance, signerFactory,
            keyService: null, custodyService: custody, custodyScopeFactory: null);
    }

    [Fact]
    public async Task PerUserTransfer_SignsWithUsersKey_ViaCustodyResolver_NotPlatform()
    {
        using var _ = RunStub(confirmedRound: 3);

        var avatarId = Guid.NewGuid();
        var walletId = Guid.NewGuid();

        // The custody resolver: records the (walletId, avatarId) it was asked to
        // resolve and signs with the USER's account key (NOT the platform key).
        Guid? resolvedWallet = null;
        Guid? resolvedAvatar = null;
        var custody = new Mock<IKeyCustodyService>();
        // The provider now routes through the consent-aware overload
        // WithSigningKeyAsync(SigningContext, sign) (tenant-consent-delegation C1).
        custody.Setup(c => c.WithSigningKeyAsync(
                It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .Returns(async (SigningContext ctx, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
            {
                resolvedWallet = ctx.WalletId;
                resolvedAvatar = ctx.AvatarId;
                // Hand the USER's private key to the signer.
                var userKey = (byte[])_userAccount.KeyPair.ClearTextPrivateKey.Clone();
                var inner = await sign(userKey);
                return new AZOAResult<AZOAResult<byte[]>> { Result = inner };
            });
        // The platform door must NOT be taken for a per-user op (either overload).
        custody.Setup(c => c.WithPlatformSigningKeyAsync(
                It.IsAny<bool>(), It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .ThrowsAsync(new InvalidOperationException(
                "C1: a per-user transfer must NOT resolve the platform key."));

        var provider = NewProvider(custody.Object);
        var userAddr = _userAccount.Address.EncodeAsString();

        var result = await provider.TransferAsync(
            tokenId: "12345",
            fromAddress: userAddr,
            toAddress: userAddr,
            amount: 1UL,
            signingContext: SigningContext.ForUser(avatarId, walletId));

        result.IsError.Should().BeFalse(result.Message);
        _submitCount.Should().Be(1, "the per-user transfer must broadcast exactly once");

        // The custody resolver was invoked with the USER's wallet + avatar — proof
        // the provider routes the right identity into the signer (not the platform).
        resolvedWallet.Should().Be(walletId);
        resolvedAvatar.Should().Be(avatarId);
        custody.Verify(c => c.WithSigningKeyAsync(
            It.Is<SigningContext>(x => x.WalletId == walletId && x.AvatarId == avatarId),
            It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()), Times.Once);
        custody.Verify(c => c.WithPlatformSigningKeyAsync(
            It.IsAny<bool>(), It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()), Times.Never);
    }

    [Fact]
    public async Task PerUserTransfer_NonOwningAvatar_IsIdorRejected_WithNoSigningSideEffect()
    {
        using var _ = RunStub(confirmedRound: 3);

        var attackerAvatarId = Guid.NewGuid();
        var walletId = Guid.NewGuid();

        var custody = new Mock<IKeyCustodyService>();
        // The custody IDOR guard: the wallet is not owned by this avatar → error
        // BEFORE the sign delegate runs (mirrors KeyCustodyService.cs:91-96).
        custody.Setup(c => c.WithSigningKeyAsync(
                It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .Returns((SigningContext ctx, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
                // sign is never invoked under an IDOR rejection (guard returns first).
                Task.FromResult(new AZOAResult<AZOAResult<byte[]>>
                {
                    IsError = true,
                    Message = "Wallet not owned by this avatar."
                }));

        var provider = NewProvider(custody.Object);
        var userAddr = _userAccount.Address.EncodeAsString();

        var result = await provider.TransferAsync(
            tokenId: "12345",
            fromAddress: userAddr,
            toAddress: userAddr,
            amount: 1UL,
            signingContext: SigningContext.ForUser(attackerAvatarId, walletId));

        result.IsError.Should().BeTrue("a non-owning avatar must be IDOR-rejected by the custody guard");
        _submitCount.Should().Be(0, "no transaction may be broadcast when signing is IDOR-rejected");
    }

    [Fact]
    public async Task PerUserTransfer_UnresolvableContext_FailsClosed_NeverPlatformFallback()
    {
        using var _ = RunStub(confirmedRound: 3);

        // No custody wired at all AND a per-user context: the provider must fail
        // closed, NOT fall back to the configured platform mnemonic.
        var config = BuildConfig();
        var signerFactory = new TransactionSignerFactory(new[] { new AlgorandTransactionSigner() });
        var provider = new AlgorandProvider(
            config, NullLogger<AlgorandProvider>.Instance, signerFactory,
            keyService: new AZOA.WebAPI.Core.WalletKeyService(config),
            custodyService: null, custodyScopeFactory: null);

        var userAddr = _userAccount.Address.EncodeAsString();
        var result = await provider.TransferAsync(
            tokenId: "12345",
            fromAddress: userAddr,
            toAddress: userAddr,
            amount: 1UL,
            signingContext: SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()));

        result.IsError.Should().BeTrue("a per-user op with no custody must fail closed");
        _submitCount.Should().Be(0, "no platform-key fallback may broadcast a user op");
    }

    // ─── In-process Algod stub (mirrors AlgorandProviderTransactTests) ───

    private IDisposable RunStub(long confirmedRound)
    {
        var cts = new CancellationTokenSource();
        var loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }

                var path = ctx.Request.Url!.AbsolutePath;
                try
                {
                    if (path.EndsWith("/v2/transactions/params"))
                    {
                        WriteJson(ctx, extra: new Dictionary<string, object?>
                        {
                            ["fee"] = 0,
                            ["min-fee"] = 1000,
                            ["last-round"] = 100,
                            ["genesis-id"] = "devnet-v1.0",
                            ["genesis-hash"] = "SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=",
                        });
                    }
                    else if (path == "/v2/transactions")
                    {
                        Interlocked.Increment(ref _submitCount);
                        WriteJson(ctx, extra: new Dictionary<string, object?> { ["txId"] = "STUBTXID" });
                    }
                    else if (path.Contains("/v2/transactions/pending/"))
                    {
                        WriteJson(ctx, extra: new Dictionary<string, object?>
                        {
                            ["confirmed-round"] = confirmedRound,
                            ["pool-error"] = "",
                        });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                    }
                }
                catch
                {
                    try { ctx.Response.Abort(); } catch { /* ignore */ }
                }
            }
        });

        return new StubScope(cts, loop);
    }

    private static void WriteJson(HttpListenerContext ctx, Dictionary<string, object?> extra)
    {
        var json = JsonSerializer.Serialize(extra);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = 200;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { /* ignore */ }
    }

    private sealed class StubScope : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _loop;
        public StubScope(CancellationTokenSource cts, Task loop) { _cts = cts; _loop = loop; }
        public void Dispose()
        {
            _cts.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }
}
