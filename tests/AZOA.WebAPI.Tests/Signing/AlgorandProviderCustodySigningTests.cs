using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Signing;
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
public class AlgorandProviderCustodySigningTests
{
    private const string BaseUrl = "http://algod.test/";
    private readonly AlgoAccount _userAccount = new();

    private int _submitCount;

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
            ["Blockchain:Chains:0:Devnet:NodeUrl"] = BaseUrl,
            ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "1000",
        })
        .Build();

    private AlgorandProvider NewProvider(IKeyCustodyService custody, HttpMessageHandler? handler = null)
    {
        var config = BuildConfig();
        var signerFactory = new TransactionSignerFactory(new[] { new AlgorandTransactionSigner() });
        return new AlgorandProvider(
            config, NullLogger<AlgorandProvider>.Instance, signerFactory,
            keyService: null, custodyService: custody, custodyScopeFactory: null,
            faucet: null, httpMessageHandler: handler);
    }

    [Fact]
    public async Task PerUserTransfer_SignsWithUsersKey_ViaCustodyResolver_NotPlatform()
    {
        using var stub = RunStub(confirmedRound: 3);

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

        var provider = NewProvider(custody.Object, stub);
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
        using var stub = RunStub(confirmedRound: 3);

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

        var provider = NewProvider(custody.Object, stub);
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
        using var stub = RunStub(confirmedRound: 3);

        // No custody wired at all AND a per-user context: the provider must fail
        // closed, NOT fall back to the configured platform mnemonic.
        var config = BuildConfig();
        var signerFactory = new TransactionSignerFactory(new[] { new AlgorandTransactionSigner() });
        var provider = new AlgorandProvider(
            config, NullLogger<AlgorandProvider>.Instance, signerFactory,
            keyService: new AZOA.WebAPI.Services.Signing.WalletKeyService(config),
            custodyService: null, custodyScopeFactory: null,
            faucet: null, httpMessageHandler: stub);

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

    private StubScope RunStub(long confirmedRound) => new(this, confirmedRound);

    private static HttpResponseMessage JsonResponse(Dictionary<string, object?> extra)
    {
        var content = new StringContent(JsonSerializer.Serialize(extra), System.Text.Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class StubScope : HttpMessageHandler
    {
        private readonly AlgorandProviderCustodySigningTests _owner;
        private readonly long _confirmedRound;

        public StubScope(AlgorandProviderCustodySigningTests owner, long confirmedRound)
        {
            _owner = owner;
            _confirmedRound = confirmedRound;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v2/transactions/params", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(new Dictionary<string, object?>
                {
                    ["fee"] = 0,
                    ["min-fee"] = 1000,
                    ["last-round"] = 100,
                    ["genesis-id"] = "devnet-v1.0",
                    ["genesis-hash"] = "SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=",
                }));
            }

            if (path == "/v2/transactions")
            {
                Interlocked.Increment(ref _owner._submitCount);
                return Task.FromResult(JsonResponse(new Dictionary<string, object?> { ["txId"] = "STUBTXID" }));
            }

            if (path.Contains("/v2/transactions/pending/", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(new Dictionary<string, object?>
                {
                    ["confirmed-round"] = _confirmedRound,
                    ["pool-error"] = "",
                }));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
