using System.Text;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>RV-L4-03: an ownership proof needs a purpose-bound challenge naming the user and wallet, so a relayed login
/// or foreign ownership signature cannot attach the address to another account.</summary>
public sealed class WalletOwnershipProofFlowTests
{
    private static readonly string Address = new Algorand.Address(new byte[32]).ToString();
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _mallory = Guid.NewGuid();
    private readonly Wallet _aliceWallet;
    private readonly Wallet _malloryWallet;
    private readonly InMemoryChallenges _challenges = new();
    private readonly Mock<IWalletOwnershipProofStore> _proofs = new();
    private readonly WalletAuthManager _auth;
    private readonly WalletManager _wallets;

    public WalletOwnershipProofFlowTests()
    {
        _aliceWallet = new Wallet { Id = Guid.NewGuid(), UserId = _alice, ChainType = "Algorand", Address = Address, WalletType = WalletType.External };
        _malloryWallet = new Wallet { Id = Guid.NewGuid(), UserId = _mallory, ChainType = "Algorand", Address = Address, WalletType = WalletType.External };
        var walletStore = new Mock<IWalletStore>();
        foreach (var wallet in new[] { _aliceWallet, _malloryWallet })
            walletStore.Setup(store => store.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<IWallet>.Success(wallet));
        _proofs.Setup(store => store.RecordAsync(It.IsAny<WalletOwnershipProof>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WalletOwnershipProof proof, CancellationToken _) => AZOAResult<WalletOwnershipProofWrite>.Success(new(proof, null)));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "super-secret-key-for-testing-only-quite-long-enough!", ["Jwt:Issuer"] = "test", ["Jwt:Audience"] = "test",
            ["AZOA:WalletEncryptionKey"] = "test-encryption-key-for-unit-tests-min-32-chars!!",
            ["CustodialAccounts:CustodyMode"] = CustodyModeResolver.PlatformMode,
        }).Build();
        _auth = new WalletAuthManager(_challenges, Mock.Of<IWalletAuthClaimTokenStore>(), Mock.Of<IUserStore>(), new KeyHolderSignatures(), config);
        _wallets = new WalletManager(walletStore.Object, Mock.Of<IHolonStore>(), Mock.Of<IBlockchainProviderFactory>(),
            new WalletKeyService(config), config, walletAuth: _auth, ownershipProofs: _proofs.Object);
    }

    [Fact]
    public async Task Key_holder_proves_their_own_wallet_with_its_ownership_challenge()
    {
        var challenge = (await _wallets.CreateOwnershipChallengeAsync(_aliceWallet.Id, _alice)).Result!;

        var proven = await _wallets.ProveOwnershipAsync(_aliceWallet.Id, _alice, Sign(challenge.Message), challenge.Message);

        proven.IsError.Should().BeFalse(proven.Message);
        challenge.Message.Should().Contain($"user:{_alice:D}").And.Contain($"wallet:{_aliceWallet.Id:D}").And.Contain("purpose:wallet-ownership-proof");
        _proofs.Verify(store => store.RecordAsync(It.Is<WalletOwnershipProof>(proof => proof.UserId == _alice && proof.WalletId == _aliceWallet.Id),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    public static TheoryData<string> Relays => new() { "login signature", "another account's ownership signature" };

    [Theory]
    [MemberData(nameof(Relays))]
    public async Task Relayed_signature_cannot_prove_the_address_for_another_account(string relayed)
    {
        var message = relayed == "login signature"
            ? (await _auth.CreateChallengeAsync(Address, "algorand")).Result!.Message
            : (await _wallets.CreateOwnershipChallengeAsync(_aliceWallet.Id, _alice)).Result!.Message;
        await _wallets.CreateOwnershipChallengeAsync(_malloryWallet.Id, _mallory);

        var withMessage = await _wallets.ProveOwnershipAsync(_malloryWallet.Id, _mallory, Sign(message), message);
        var signatureOnly = await _wallets.ProveOwnershipAsync(_malloryWallet.Id, _mallory, Sign(message), null);

        withMessage.IsError.Should().BeTrue();
        signatureOnly.IsError.Should().BeTrue();
        _proofs.Verify(store => store.RecordAsync(It.IsAny<WalletOwnershipProof>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ownership_challenge_cannot_be_used_to_log_in()
    {
        var challenge = (await _wallets.CreateOwnershipChallengeAsync(_aliceWallet.Id, _alice)).Result!;

        var login = await _auth.VerifyAsync(Address, "algorand", Sign(challenge.Message), challenge.Message);

        login.IsError.Should().BeTrue();
    }

    /// <summary>Models a real wallet: a signature is valid only over the exact bytes the key holder signed.</summary>
    private static string Sign(string message) => Convert.ToBase64String(Encoding.UTF8.GetBytes("signed:" + message));

    private sealed class KeyHolderSignatures : IWalletSignatureVerifier
    {
        public bool Verify(string chainType, string address, byte[] message, byte[] signature)
            => address == Address && signature.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("signed:" + Encoding.UTF8.GetString(message)));
    }

    private sealed class InMemoryChallenges : IWalletAuthChallengeStore
    {
        private readonly List<WalletAuthChallenge> _rows = [];

        public Task<AZOAResult<WalletAuthChallenge>> CreateAsync(WalletAuthChallenge challenge, CancellationToken ct = default)
        {
            _rows.Add(challenge);
            return Task.FromResult(AZOAResult<WalletAuthChallenge>.Success(challenge));
        }

        public Task<AZOAResult<WalletAuthChallenge>> GetByNonceAsync(string nonce, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<WalletAuthChallenge> { Result = _rows.SingleOrDefault(row => row.Nonce == nonce) });

        public Task<AZOAResult<WalletAuthChallenge>> GetLatestLiveByAddressAsync(string address, string chainType, DateTime now, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<WalletAuthChallenge>
            {
                Result = _rows.Where(row => Live(row, address, chainType, now)).OrderByDescending(row => row.CreatedAt).ThenByDescending(row => _rows.IndexOf(row)).FirstOrDefault(),
            });

        public Task<AZOAResult<bool>> TryConsumeAsync(string nonce, DateTime now, CancellationToken ct = default)
        {
            var row = _rows.SingleOrDefault(item => item.Nonce == nonce && item.ConsumedAt is null && item.ExpiresAt > now);
            if (row is not null) row.ConsumedAt = now;
            return Task.FromResult(AZOAResult<bool>.Success(row is not null));
        }

        public Task<AZOAResult<int>> CountLiveByAddressAsync(string address, string chainType, DateTime now, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<int>.Success(_rows.Count(row => Live(row, address, chainType, now))));

        private static bool Live(WalletAuthChallenge row, string address, string chainType, DateTime now)
            => row.Address == address && row.ChainType == chainType && row.ConsumedAt is null && row.ExpiresAt > now;
    }
}
