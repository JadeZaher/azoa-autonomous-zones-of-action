using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests;

public class WalletManagerTests
{
    private readonly Mock<IWalletStore> _walletStore;
    private readonly Mock<IHolonStore> _holonStore;
    private readonly Mock<IBlockchainProviderFactory> _chainFactory;
    private readonly WalletManager _manager;
    private readonly WalletKeyService _keyService;
    private readonly IConfiguration _config;

    public WalletManagerTests()
    {
        _walletStore = new Mock<IWalletStore>();
        _holonStore = new Mock<IHolonStore>();

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = "test-encryption-key-for-unit-tests-min-32-chars!!",
                ["Blockchain:DefaultNetwork"] = "Devnet",
                ["Blockchain:Faucet:DefaultAmount"] = "5",
                // OI-12: BootstrapWalletAsync now answers the custody predicate itself, so a
                // fixture that configures no mode gets the fail-closed refusal, not a wallet.
                ["CustodialAccounts:CustodyMode"] = CustodyModeResolver.PlatformMode
            })
            .Build();
        _chainFactory = new Mock<IBlockchainProviderFactory>();
        _keyService = new WalletKeyService(_config);
        _manager = new WalletManager(_walletStore.Object, _holonStore.Object, _chainFactory.Object, _keyService, _config);
    }

    /// <summary>
    /// Faucet operations are provider-scoped: stub a provider that the factory hands
    /// out for <paramref name="chainType"/>, with its faucet capability + dispense
    /// outcome under the test's control.
    /// </summary>
    private Mock<IBlockchainProvider> GivenFaucetProvider(
        string chainType, bool supportsFaucet = true)
    {
        var provider = new Mock<IBlockchainProvider>();
        provider.SetupGet(p => p.ChainType).Returns(chainType);
        provider.SetupGet(p => p.SupportsFaucet).Returns(supportsFaucet);
        _chainFactory
            .Setup(f => f.GetProvider(chainType, It.IsAny<ChainNetwork>()))
            .Returns(provider.Object);
        return provider;
    }

    [Fact]
    public async Task CreateAsync_ShouldSetUserIdAndSave()
    {
        _walletStore.Setup(p => p.GetByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = Array.Empty<IWallet>() });
        _walletStore.Setup(p => p.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new AZOAResult<IWallet> { Result = w });

        var model = new WalletCreateModel { ChainType = "Solana", Address = "addr1", IsDefault = false };
        var userId = Guid.NewGuid();

        var result = await _manager.CreateAsync(model, userId);

        result.IsError.Should().BeFalse();
        result.Result!.UserId.Should().Be(userId);
        result.Result.Address.Should().Be("addr1");
    }

    [Fact]
    public async Task CreateAsync_DuplicateAddressPerChain_ReturnsError()
    {
        var owner = Guid.NewGuid();
        var existing = new Wallet { Id = Guid.NewGuid(), UserId = owner, ChainType = "Solana", Address = "addr1" };
        _walletStore.Setup(p => p.GetByUserAsync(owner, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { existing } });

        var model = new WalletCreateModel { ChainType = "Solana", Address = "addr1" };
        var result = await _manager.CreateAsync(model, owner);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("already exists");
    }

    [Fact]
    public async Task CreateAsync_WithDefault_ShouldUnsetPreviousDefault()
    {
        var prev = new Wallet { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), ChainType = "Solana", Address = "old", IsDefault = true };
        _walletStore.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { prev } });
        _walletStore.Setup(p => p.GetByUserAsync(prev.UserId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { prev } });
        _walletStore.Setup(p => p.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new AZOAResult<IWallet> { Result = w });

        var model = new WalletCreateModel { ChainType = "Solana", Address = "new", IsDefault = true };
        var result = await _manager.CreateAsync(model, prev.UserId);

        result.IsError.Should().BeFalse();
        _walletStore.Verify(p => p.UpsertAsync(It.Is<IWallet>(w => w.Id == prev.Id && !w.IsDefault), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Audit-1 M2 / RV-L4-09: registering an address proves nothing, so only another user's STILL-VALID proof blocks a create
    /// (the store answers validity; a stale proof whose holder removed the wallet leaves the address addable by the new key holder).</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task CreateAsync_IsBlockedOnlyByAnotherUsersOwnershipProof(bool provenElsewhere, bool refused)
    {
        var caller = Guid.NewGuid();
        _walletStore.Setup(p => p.GetByUserAsync(caller, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = Array.Empty<IWallet>() });
        _walletStore.Setup(p => p.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new AZOAResult<IWallet> { Result = w });
        var proofs = new Mock<IWalletOwnershipProofStore>();
        proofs.Setup(p => p.IsHeldByAnotherUserAsync("Solana", "addr1", caller, It.IsAny<CancellationToken>())).ReturnsAsync(provenElsewhere);
        var manager = new WalletManager(_walletStore.Object, _holonStore.Object, _chainFactory.Object, _keyService, _config,
            ownershipProofs: proofs.Object);

        var result = await manager.CreateAsync(new WalletCreateModel { ChainType = "Solana", Address = "addr1" }, caller);

        result.IsError.Should().Be(refused, result.Message);
        _walletStore.Verify(p => p.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()), refused ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task BootstrapWalletAsync_RetryReturnsSameCreateOnlyWalletWithoutSerializingKeyMaterial()
    {
        var userId = Guid.NewGuid();
        IWallet? persisted = null;
        _walletStore.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => persisted is not null && persisted.Id == id
                ? AZOAResult<IWallet>.Success(persisted)
                : AZOAResult<IWallet>.Failure("Wallet not found."));
        _walletStore.Setup(s => s.CreateIfAbsentAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IWallet wallet, CancellationToken _) =>
            {
                persisted ??= wallet;
                return AZOAResult<IWallet>.Success(persisted);
            });

        var request = new WalletGenerateRequest { ChainType = "Algorand", IsDefault = false };
        var first = await _manager.BootstrapWalletAsync(request, userId);
        var retry = await _manager.BootstrapWalletAsync(request, userId);

        first.IsError.Should().BeFalse();
        retry.IsError.Should().BeFalse();
        retry.Result!.Id.Should().Be(first.Result!.Id);
        retry.Result.Address.Should().Be(first.Result.Address);
        retry.Result.UserId.Should().Be(userId);
        retry.Result.WalletType.Should().Be(WalletType.Platform);
        retry.Result.EncryptedSeedPhrase.Should().BeNull();
        _walletStore.Verify(s => s.CreateIfAbsentAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()), Times.Once);

        var json = System.Text.Json.JsonSerializer.Serialize(first.Result);
        json.Should().NotContain("EncryptedPrivateKey");
        json.Should().NotContain("EncryptedSeedPhrase");
    }

    [Fact]
    public async Task BootstrapWalletAsync_DeterministicIdCollisionWithDifferentOwnerFailsClosed()
    {
        _walletStore.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IWallet>.Success(new Wallet
            {
                UserId = Guid.NewGuid(),
                ChainType = "Algorand",
                WalletType = WalletType.Platform
            }));

        var result = await _manager.BootstrapWalletAsync(
            new WalletGenerateRequest { ChainType = "Algorand" }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("different wallet");
        _walletStore.Verify(s => s.CreateIfAbsentAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_ShouldApplyPartialChanges()
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), ChainType = "Solana", Address = "addr", Label = "Old", IsDefault = false };
        _walletStore.Setup(p => p.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });
        _walletStore.Setup(p => p.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new AZOAResult<IWallet> { Result = w });

        var result = await _manager.UpdateAsync(wallet.Id, new WalletUpdateModel { Label = "New" }, wallet.UserId);

        result.IsError.Should().BeFalse();
        result.Result!.Label.Should().Be("New");
        result.Result.IsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task SetDefaultAsync_ShouldSwapDefaultFlag()
    {
        var userId = Guid.NewGuid();
        var prev = new Wallet { Id = Guid.NewGuid(), UserId = userId, ChainType = "Solana", Address = "old", IsDefault = true };
        var current = new Wallet { Id = Guid.NewGuid(), UserId = userId, ChainType = "Solana", Address = "new", IsDefault = false };

        _walletStore.Setup(p => p.GetByIdAsync(current.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { Result = current });
        _walletStore.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { prev } });
        _walletStore.Setup(p => p.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IWallet w, CancellationToken _) => new AZOAResult<IWallet> { Result = w });

        var result = await _manager.SetDefaultAsync(userId, current.Id);

        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        current.IsDefault.Should().BeTrue();
        _walletStore.Verify(p => p.UpsertAsync(It.Is<IWallet>(w => w.Id == prev.Id && !w.IsDefault), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetDefaultAsync_WrongUser_ReturnsError()
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), ChainType = "Solana", Address = "addr" };
        _walletStore.Setup(p => p.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });

        var result = await _manager.SetDefaultAsync(Guid.NewGuid(), wallet.Id);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not owned");
    }

    [Fact]
    public async Task GetPortfolioAsync_ShouldReturnStubWithNfts()
    {
        var userId = Guid.NewGuid();
        var wallet = new Wallet { Id = Guid.NewGuid(), UserId = userId, ChainType = "Solana", Address = "addr1" };
        var nft = new Holon { Id = Guid.NewGuid(), UserId = userId, AssetType = "NFT", Name = "MyNFT", TokenId = "123", Metadata = new Dictionary<string, string> { ["image"] = "ipfs://img" } };

        _walletStore.Setup(p => p.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });
        _holonStore.Setup(p => p.QueryAsync(null, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { nft } });

        var result = await _manager.GetPortfolioAsync(wallet.Id, userId);

        result.IsError.Should().BeFalse();
        result.Result!.WalletId.Should().Be(wallet.Id);
        result.Result.Symbol.Should().Be("SOL");
        result.Result.Nfts.Should().HaveCount(1);
        result.Result.Nfts.First().Name.Should().Be("MyNFT");
    }

    [Fact]
    public async Task QueryAsync_WithFilters_ShouldReturnFiltered()
    {
        var w1 = new Wallet { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), ChainType = "Solana", IsDefault = true };
        var w2 = new Wallet { Id = Guid.NewGuid(), UserId = w1.UserId, ChainType = "Algorand", IsDefault = false };

        _walletStore.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { w1, w2 } });

        var result = await _manager.QueryAsync(new WalletQueryRequest { UserId = w1.UserId, IsDefault = true }, w1.UserId);

        result.IsError.Should().BeFalse();
        result.Result.Should().ContainSingle(w => w.Id == w1.Id);
    }

    // ─── TopUpAsync (faucet) ───

    private Wallet GivenOwnedAlgorandWallet(Guid userId)
    {
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ChainType = "Algorand",
            Address = "RECIPIENTADDRESSALGORAND234567ABCDEFGHIJKLMNOPQRSTUVWXYZ23",
            WalletType = WalletType.Platform
        };
        _walletStore.Setup(p => p.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });
        return wallet;
    }

    [Fact]
    public async Task TopUpAsync_WalletNotFound_ReturnsError()
    {
        var id = Guid.NewGuid();
        _walletStore.Setup(p => p.GetByIdAsync(id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { IsError = true, Result = null });

        var result = await _manager.TopUpAsync(id, null, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task TopUpAsync_WrongUser_ReturnsError()
    {
        var wallet = GivenOwnedAlgorandWallet(Guid.NewGuid());

        var result = await _manager.TopUpAsync(wallet.Id, null, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not owned");
    }

    [Fact]
    public async Task TopUpAsync_OnMainnet_IsHardBlocked()
    {
        var mainnetConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = "test-encryption-key-for-unit-tests-min-32-chars!!",
                ["Blockchain:DefaultNetwork"] = "Mainnet"
            })
            .Build();
        var chainFactory = new Mock<IBlockchainProviderFactory>();
        var provider = new Mock<IBlockchainProvider>();
        chainFactory.Setup(f => f.GetProvider("Algorand", It.IsAny<ChainNetwork>())).Returns(provider.Object);
        var manager = new WalletManager(_walletStore.Object, _holonStore.Object, chainFactory.Object,
            new WalletKeyService(mainnetConfig), mainnetConfig);

        var userId = Guid.NewGuid();
        var wallet = GivenOwnedAlgorandWallet(userId);

        var result = await manager.TopUpAsync(wallet.Id, 5m, userId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("mainnet");
        // Mainnet is hard-blocked BEFORE the provider faucet path is ever reached.
        provider.Verify(p => p.DispenseFromFaucetAsync(
            It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TopUpAsync_AlgorandFaucetNotConfigured_ReturnsClearError()
    {
        var userId = Guid.NewGuid();
        var wallet = GivenOwnedAlgorandWallet(userId);
        var provider = GivenFaucetProvider("Algorand");
        provider
            .Setup(p => p.DispenseFromFaucetAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<FaucetDispenseResult>
            {
                IsError = true,
                Message = "Algorand faucet is not configured (set Blockchain:Faucet:Algorand:Mnemonic)."
            });

        var result = await _manager.TopUpAsync(wallet.Id, 5m, userId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Blockchain:Faucet:Algorand:Mnemonic");
    }

    [Fact]
    public async Task TopUpAsync_AlgorandSuccess_ReturnsTxHashAndUsesDefaultAmount()
    {
        var userId = Guid.NewGuid();
        var wallet = GivenOwnedAlgorandWallet(userId);
        var provider = GivenFaucetProvider("Algorand");
        provider
            .Setup(p => p.DispenseFromFaucetAsync(
                wallet.Address, 5m, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<FaucetDispenseResult>
            {
                Result = new FaucetDispenseResult("TXHASH123", IsClientSide: false, "Dispensed 5 test ALGO.")
            });

        var result = await _manager.TopUpAsync(wallet.Id, null, userId);

        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
        var payload = result.Result!.GetType();
        payload.GetProperty("txHash")!.GetValue(result.Result).Should().Be("TXHASH123");
        payload.GetProperty("amount")!.GetValue(result.Result).Should().Be(5m);
        payload.GetProperty("chain")!.GetValue(result.Result).Should().Be("Algorand");
        payload.GetProperty("network")!.GetValue(result.Result).Should().Be("Devnet");
    }

    [Fact]
    public async Task TopUpAsync_AlgorandFaucetErrors_ReturnsError()
    {
        var userId = Guid.NewGuid();
        var wallet = GivenOwnedAlgorandWallet(userId);
        var provider = GivenFaucetProvider("Algorand");
        provider
            .Setup(p => p.DispenseFromFaucetAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<FaucetDispenseResult>
            {
                IsError = true,
                Message = "Algorand faucet failed: algod unreachable"
            });

        var result = await _manager.TopUpAsync(wallet.Id, 5m, userId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("algod unreachable");
    }

    [Fact]
    public async Task TopUpAsync_ProviderThrows_ReturnsErrorNotThrows()
    {
        var userId = Guid.NewGuid();
        var wallet = GivenOwnedAlgorandWallet(userId);
        var provider = GivenFaucetProvider("Algorand");
        provider
            .Setup(p => p.DispenseFromFaucetAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _manager.TopUpAsync(wallet.Id, 5m, userId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("boom");
    }

    [Fact]
    public async Task TopUpAsync_Solana_ReturnsClientSideMessageWithoutError()
    {
        var userId = Guid.NewGuid();
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ChainType = "Solana",
            Address = "SoLaNaAddr1111111111111111111111111111111111",
            WalletType = WalletType.Platform
        };
        _walletStore.Setup(p => p.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });
        var provider = GivenFaucetProvider("Solana");
        provider
            .Setup(p => p.DispenseFromFaucetAsync(
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<FaucetDispenseResult>
            {
                Result = new FaucetDispenseResult(null, IsClientSide: true,
                    "Solana devnet/testnet top-up is performed client-side via RPC airdrop (requestAirdrop).")
            });

        var result = await _manager.TopUpAsync(wallet.Id, 1m, userId);

        result.IsError.Should().BeFalse();
        result.Message.Should().Contain("client-side");
    }

    [Fact]
    public async Task TopUpAsync_UnsupportedChain_ReturnsError()
    {
        var userId = Guid.NewGuid();
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ChainType = "Ethereum",
            Address = "0xabc",
            WalletType = WalletType.Platform
        };
        _walletStore.Setup(p => p.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IWallet> { Result = wallet });
        // A provider with no faucet path (SupportsFaucet == false) ⇒ "not supported".
        GivenFaucetProvider("Ethereum", supportsFaucet: false);

        var result = await _manager.TopUpAsync(wallet.Id, 1m, userId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not supported");
    }
}

/// <summary>
/// OI-12: the custody predicate is answered where the key is MINTED, not at whichever
/// caller happens to exist today.
/// </summary>
/// <remarks>
/// <c>BootstrapWalletAsync</c> is on the public <see cref="IWalletManager"/> surface. The
/// argument that its "sole caller" (<c>TenantCustodialAccountManager.ConvergeAccountAsync</c>)
/// already checks <c>WalletProvisioningReady</c> describes today's call graph, not the
/// contract - a second caller inherits nothing from it. These tests assert refusal AT THE
/// COUNTER: the store is never asked to persist a wallet, so no key was minted.
/// </remarks>
public class BootstrapWalletCustodyGateTests
{
    private static IConfiguration ConfigWithCustodyMode(string? custodyMode)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AZOA:WalletEncryptionKey"] = "test-encryption-key-for-unit-tests-min-32-chars!!",
            ["Blockchain:DefaultNetwork"] = "Devnet"
        };
        if (custodyMode is not null)
            settings[CustodyModeResolver.CustodyModeConfigurationPath] = custodyMode;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static (WalletManager manager, Mock<IWalletStore> store) NewManager(string? custodyMode)
    {
        var config = ConfigWithCustodyMode(custodyMode);
        var store = new Mock<IWalletStore>();
        store.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IWallet>.Failure("Wallet not found."));
        store.Setup(s => s.CreateIfAbsentAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IWallet wallet, CancellationToken _) => AZOAResult<IWallet>.Success(wallet));

        var manager = new WalletManager(
            store.Object,
            new Mock<IHolonStore>().Object,
            new Mock<IBlockchainProviderFactory>().Object,
            new WalletKeyService(config),
            config);

        return (manager, store);
    }

    /// <summary>
    /// The blank case is first on purpose: an env var set to "" is the shape a deploy
    /// produces most easily, and a default is only a control if the code resolves it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(CustodyModeResolver.DisabledMode)]
    [InlineData(CustodyModeResolver.KmsHsmMode)]
    [InlineData("SomethingNobodyImplements")]
    public async Task BootstrapWalletAsync_WithoutUsableCustody_RefusesAndMintsNothing(string? custodyMode)
    {
        var (manager, store) = NewManager(custodyMode);

        var result = await manager.BootstrapWalletAsync(
            new WalletGenerateRequest { ChainType = "Algorand" }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(WalletManager.WalletCustodyUnavailable);
        store.Verify(
            s => s.CreateIfAbsentAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>The control: the refusal above is custody, not a manager that cannot mint.</summary>
    [Fact]
    public async Task BootstrapWalletAsync_WithUsableCustody_Mints()
    {
        var (manager, store) = NewManager(CustodyModeResolver.PlatformMode);

        var result = await manager.BootstrapWalletAsync(
            new WalletGenerateRequest { ChainType = "Algorand", IsDefault = false }, Guid.NewGuid());

        result.IsError.Should().BeFalse(result.Message);
        store.Verify(
            s => s.CreateIfAbsentAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static (WalletManager manager, Mock<IWalletStore> store, IConfiguration config) NewSimulatedChainManager(
        string custodyMode, string blockchainMode, string environmentName)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AZOA:WalletEncryptionKey"] = "test-encryption-key-for-unit-tests-min-32-chars!!",
            [CustodyModeResolver.CustodyModeConfigurationPath] = custodyMode,
            [CustodyModeResolver.BlockchainModeConfigurationPath] = blockchainMode,
        }).Build();
        var store = new Mock<IWalletStore>();
        store.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IWallet>.Failure("Wallet not found."));
        store.Setup(s => s.CreateIfAbsentAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IWallet wallet, CancellationToken _) => AZOAResult<IWallet>.Success(wallet));
        var environment = Mock.Of<Microsoft.Extensions.Hosting.IHostEnvironment>(e => e.EnvironmentName == environmentName);
        return (new WalletManager(store.Object, new Mock<IHolonStore>().Object, new Mock<IBlockchainProviderFactory>().Object,
            new WalletKeyService(config), config, environment), store, config);
    }

    /// <summary>The fungible genesis path in Blockchain:Mode=Simulated: the wallet row is one the Simulated provider accepts and its key controls.</summary>
    [Fact]
    public async Task BootstrapWalletAsync_SimulatedChainOnSimulatedDevelopmentNode_MintsAWalletItsKeyControls()
    {
        var (manager, _, config) = NewSimulatedChainManager(CustodyModeResolver.DevelopmentOnlyMode, "Simulated", "Development");
        var userId = Guid.NewGuid();

        var result = await manager.BootstrapWalletAsync(new WalletGenerateRequest { ChainType = "simulated" }, userId);

        result.IsError.Should().BeFalse(result.Message);
        var wallet = result.Result!;
        wallet.ChainType.Should().Be("Simulated");
        wallet.Id.Should().Be(AZOA.WebAPI.Helpers.WalletBootstrapIdentity.For(userId, "Simulated"));
        var provider = new AZOA.WebAPI.Providers.Blockchain.Simulated.SimulatedBlockchainProvider(config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AZOA.WebAPI.Providers.Blockchain.Simulated.SimulatedBlockchainProvider>.Instance);
        (await provider.ValidateAddressAsync(wallet.Address)).Result.Should().BeTrue();
        var key = new WalletKeyService(config).DecryptPrivateKeyBytes(wallet.EncryptedPrivateKey!);
        WalletKeyService.SimulatedAddressFor(key).Should().Be(wallet.Address);
        WalletKeyService.SimulatedPublicKeyHex(key).Should().Be(wallet.PublicKey);
    }

    /// <summary>A live node never mints a throwaway Simulated key, even with usable custody.</summary>
    [Fact]
    public async Task BootstrapWalletAsync_SimulatedChainOnLiveNode_RefusesAndMintsNothing()
    {
        var (manager, store, _) = NewSimulatedChainManager(CustodyModeResolver.PlatformMode, "Live", "Production");

        var result = await manager.BootstrapWalletAsync(new WalletGenerateRequest { ChainType = "Simulated" }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Be(WalletManager.SimulatedChainOffSimulatedNode);
        store.Verify(s => s.CreateIfAbsentAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()), Times.Never);
        (await manager.BootstrapWalletAsync(new WalletGenerateRequest { ChainType = "Algorand" }, Guid.NewGuid()))
            .IsError.Should().BeFalse("the refusal is the Simulated chain on a live node, not custody");
    }
}

/// <summary>
/// OI-72: the context-less <c>WithPlatformSigningKeyAsync(bool, Func)</c> overload ran no
/// consent gate, unlike its three ctx-carrying siblings.
/// </summary>
/// <remarks>
/// <para><b>The recorded grep.</b> At AZOA <c>195c266</c>,
/// <c>grep -rn "WithPlatformSigningKeyAsync" --include=*.cs .</c> found NO production caller
/// of the two-argument overload. Every value path uses a sibling that carries a
/// <c>SigningContext</c>: <c>AlgorandProvider.cs:2471</c>, <c>EthereumProvider.cs:1611</c>,
/// <c>SolanaProvider.cs:989</c>, <c>PlatformReserveService.cs:137</c> and <c>:203</c>,
/// <c>ProjectTreasuryService.cs:176</c> and <c>:427</c>. So no tenant-driven operation
/// reaches it, and with no context it cannot express an acting tenant, a grantor or a scope:
/// it is operator-only by construction.</para>
/// <para>The overload is declared on the public <c>IKeyCustodyService</c>, so it cannot be
/// made <c>internal</c> without changing that interface. It is pinned to an explicit operator
/// context and now runs the same live gate instead, which is what these tests assert - a
/// uniform seam in code rather than a comment claiming uniformity.</para>
/// </remarks>
public class PlatformSigningOverloadConsentSeamTests
{
    private const string GateRefusal = "CONSENT_DENIED_BY_TEST_GATE";

    private static KeyCustodyService NewCustodyService(bool consentAllowed)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = "test-encryption-key-for-unit-tests-min-32-chars!!"
            })
            .Build();

        var gate = new Mock<ITenantConsentGate>();
        gate.Setup(g => g.EnsureAllowedAsync(It.IsAny<SigningContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(consentAllowed
                ? new AZOAResult<bool> { Result = true }
                : new AZOAResult<bool> { IsError = true, Message = GateRefusal });

        return new KeyCustodyService(
            new Mock<IWalletStore>().Object, new WalletKeyService(config), config, gate.Object);
    }

    [Fact]
    public async Task ContextLessPlatformOverload_DeniedByGate_RefusesWithoutSigning()
    {
        var signed = false;

        var result = await NewCustodyService(consentAllowed: false)
            .WithPlatformSigningKeyAsync<int>(isPlatformContext: true, _ =>
            {
                signed = true;
                return Task.FromResult(1);
            });

        result.IsError.Should().BeTrue();
        result.Message.Should().Be(GateRefusal);
        signed.Should().BeFalse("the gate must return before any decrypt");
    }

    /// <summary>
    /// The discriminator: with the gate allowing, the call gets PAST it and fails for a
    /// different reason (no platform key is configured here). Without this, the test above
    /// would also pass on a method that refused everything.
    /// </summary>
    [Fact]
    public async Task ContextLessPlatformOverload_AllowedByGate_FailsFurtherDownNotAtTheGate()
    {
        var result = await NewCustodyService(consentAllowed: true)
            .WithPlatformSigningKeyAsync<int>(isPlatformContext: true, _ => Task.FromResult(1));

        result.IsError.Should().BeTrue();
        result.Message.Should().NotContain(GateRefusal);
        result.Message.Should().Contain("No platform signing key configured",
            "the call must reach key resolution, so the deny above came from the gate");
    }

    [Fact]
    public void ContextLessPlatformOverload_IsPinnedToAnOperatorContext()
    {
        // No acting tenant, no grantor, no scope: the overload cannot carry tenant authority.
        KeyCustodyService.OperatorOnlyContext.IsPlatform.Should().BeTrue();
        KeyCustodyService.OperatorOnlyContext.ActingTenantId.Should().Be(Guid.Empty);
        KeyCustodyService.OperatorOnlyContext.GrantorUserId.Should().Be(Guid.Empty);
        KeyCustodyService.OperatorOnlyContext.Scope.Should().BeNull();
        KeyCustodyService.OperatorOnlyContext.ChainType.Should().Be(KeyCustodyService.LegacyPlatformChainType);
    }
}
