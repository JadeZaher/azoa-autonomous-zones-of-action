using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;

namespace AZOA.WebAPI.Tests.Custody;

/// <summary>
/// The node's one custody answer. Two properties are locked here: the predicate
/// recognises a NON-development mode, so a production node can hold a key at all, and
/// <see cref="WalletManager.GenerateWalletAsync"/> actually READS that answer — a
/// resolver with no reader would be a claim, not a control.
/// </summary>
public sealed class CustodyModeResolverTests
{
    private const string UsableEncryptionKey = "a-node-custody-secret-that-is-long-enough";
    private const string TooShortEncryptionKey = "short-secret";

    // ─── Recognising the modes ───────────────────────────────────────────────

    [Theory]
    [InlineData("Platform")]
    [InlineData("Node")]
    [InlineData("platform")]
    [InlineData("  node  ")]
    public void Non_development_custody_is_available_outside_development_when_the_key_is_present(
        string configuredMode)
    {
        var decision = CustodyModeResolver.Resolve(
            Configuration(custodyMode: configuredMode, blockchainMode: "Live"),
            EnvironmentNamed(Environments.Production));

        decision.Mode.Should().Be(RecognisedCustodyMode.PlatformManaged);
        decision.Available.Should().BeTrue(
            "a node that recognises no production custody mode can never provision a wallet");
        decision.UnavailableReason.Should().BeNull();
    }

    [Fact]
    public void Non_development_custody_without_a_usable_key_names_the_key()
    {
        var decision = CustodyModeResolver.Resolve(
            Configuration(
                custodyMode: "Platform",
                blockchainMode: "Live",
                encryptionKey: TooShortEncryptionKey),
            EnvironmentNamed(Environments.Production));

        decision.Available.Should().BeFalse();
        decision.UnavailableReason.Should().Be(CustodyModeResolver.WalletEncryptionKeyUnusableReason);
    }

    [Fact]
    public void Non_development_custody_with_no_key_at_all_names_the_key()
    {
        var decision = CustodyModeResolver.Resolve(
            Configuration(custodyMode: "Node", blockchainMode: "Live", encryptionKey: null),
            EnvironmentNamed(Environments.Production));

        decision.Available.Should().BeFalse();
        decision.UnavailableReason.Should().Be(CustodyModeResolver.WalletEncryptionKeyUnusableReason);
    }

    [Fact]
    public void Non_development_custody_refuses_simulated_settlement()
    {
        var decision = CustodyModeResolver.Resolve(
            Configuration(custodyMode: "Platform", blockchainMode: "Simulated"),
            EnvironmentNamed(Environments.Production));

        decision.Available.Should().BeFalse();
        decision.UnavailableReason.Should().Be(CustodyModeResolver.PlatformCustodyInSimulatedModeReason);
    }

    [Fact]
    public void Development_custody_is_available_only_on_a_simulated_development_host()
    {
        var decision = CustodyModeResolver.Resolve(
            Configuration(custodyMode: "DevelopmentOnly", blockchainMode: "Simulated"),
            EnvironmentNamed(Environments.Development));

        decision.Mode.Should().Be(RecognisedCustodyMode.DevelopmentOnly);
        decision.Available.Should().BeTrue();
    }

    // Literals, not Environments.*: those are static readonly fields, not constants.
    [Theory]
    [InlineData("Production", "Simulated")]
    [InlineData("Staging", "Simulated")]
    [InlineData("Development", "Live")]
    public void Development_custody_never_leaves_a_simulated_development_host(
        string environmentName, string blockchainMode)
    {
        var decision = CustodyModeResolver.Resolve(
            Configuration(custodyMode: "DevelopmentOnly", blockchainMode: blockchainMode),
            EnvironmentNamed(environmentName));

        decision.Available.Should().BeFalse();
        decision.UnavailableReason.Should()
            .Be(CustodyModeResolver.DevelopmentCustodyOutsideDevelopmentReason);
    }

    [Fact]
    public void Development_custody_with_a_short_key_names_the_key_rather_than_the_environment()
    {
        var decision = CustodyModeResolver.Resolve(
            Configuration(
                custodyMode: "DevelopmentOnly",
                blockchainMode: "Simulated",
                encryptionKey: TooShortEncryptionKey),
            EnvironmentNamed(Environments.Development));

        decision.Available.Should().BeFalse();
        decision.UnavailableReason.Should().Be(CustodyModeResolver.WalletEncryptionKeyUnusableReason);
    }

    [Fact]
    public void A_missing_host_environment_fails_the_development_mode_closed()
    {
        var decision = CustodyModeResolver.Resolve(
            Configuration(custodyMode: "DevelopmentOnly", blockchainMode: "Simulated"),
            environment: null);

        decision.Available.Should().BeFalse("an unknown host is not a development host");
        decision.UnavailableReason.Should()
            .Be(CustodyModeResolver.DevelopmentCustodyOutsideDevelopmentReason);
    }

    [Fact]
    public void A_missing_host_environment_does_not_block_the_environment_independent_node_mode()
    {
        var decision = CustodyModeResolver.Resolve(
            Configuration(custodyMode: "Platform", blockchainMode: "Live"),
            environment: null);

        decision.Available.Should().BeTrue();
    }

    // ─── Refusing everything else ────────────────────────────────────────────

    [Fact]
    public void Disabled_custody_is_reported_as_disabled()
    {
        var decision = CustodyModeResolver.Resolve(
            Configuration(custodyMode: "Disabled", blockchainMode: "Live"),
            EnvironmentNamed(Environments.Production));

        decision.Mode.Should().Be(RecognisedCustodyMode.Disabled);
        decision.Available.Should().BeFalse();
        decision.UnavailableReason.Should().Be(CustodyModeResolver.CustodyDisabledReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_custody_mode_is_off_rather_than_on(string? configuredMode)
    {
        var decision = CustodyModeResolver.Resolve(
            Configuration(custodyMode: configuredMode, blockchainMode: "Live"),
            EnvironmentNamed(Environments.Production));

        decision.Mode.Should().Be(RecognisedCustodyMode.Disabled);
        decision.ConfiguredMode.Should().Be(CustodyModeResolver.DisabledMode);
        decision.Available.Should().BeFalse("silence is never permission to hold a key");
    }

    [Fact]
    public void The_unbuilt_kms_adapter_is_named_rather_than_silently_unavailable()
    {
        var decision = CustodyModeResolver.Resolve(
            Configuration(custodyMode: "KmsHsm", blockchainMode: "Live"),
            EnvironmentNamed(Environments.Production));

        decision.Mode.Should().Be(RecognisedCustodyMode.KmsHsm);
        decision.Available.Should().BeFalse();
        decision.UnavailableReason.Should().Be(CustodyModeResolver.KmsHsmAdapterMissingReason);
    }

    [Fact]
    public void An_unrecognised_mode_names_itself_and_fails_closed()
    {
        var decision = CustodyModeResolver.Resolve(
            Configuration(custodyMode: "Vault", blockchainMode: "Live"),
            EnvironmentNamed(Environments.Production));

        decision.Mode.Should().Be(RecognisedCustodyMode.Unrecognised);
        decision.Available.Should().BeFalse();
        decision.UnavailableReason.Should().Be(CustodyModeResolver.UnrecognisedModeReason("Vault"));
    }

    // ─── The reader: WalletManager.GenerateWalletAsync consults the resolver ──

    [Fact]
    public async Task Wallet_generation_is_refused_when_custody_is_unavailable()
    {
        var walletStore = new Mock<IWalletStore>();
        var manager = WalletManagerUnder(
            Configuration(custodyMode: "Disabled", blockchainMode: "Live"),
            EnvironmentNamed(Environments.Production),
            walletStore);

        var result = await manager.GenerateWalletAsync(
            new WalletGenerateRequest { ChainType = "Algorand" }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(WalletManager.WalletCustodyUnavailable);
        result.Message.Should().Contain(CustodyModeResolver.CustodyDisabledReason);
        walletStore.Verify(
            store => store.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no key material may be generated or persisted when custody is refused");
    }

    [Fact]
    public async Task Wallet_generation_is_refused_when_the_configured_mode_is_unrecognised()
    {
        var walletStore = new Mock<IWalletStore>();
        var manager = WalletManagerUnder(
            Configuration(custodyMode: "SomeFutureVault", blockchainMode: "Live"),
            EnvironmentNamed(Environments.Production),
            walletStore);

        var result = await manager.GenerateWalletAsync(
            new WalletGenerateRequest { ChainType = "Algorand" }, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("SomeFutureVault");
        walletStore.Verify(
            store => store.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Wallet_generation_proceeds_under_a_recognised_node_custody_mode()
    {
        var walletStore = GivenAnEmptyWalletStore();
        var manager = WalletManagerUnder(
            Configuration(custodyMode: "Platform", blockchainMode: "Live"),
            EnvironmentNamed(Environments.Production),
            walletStore);
        var userId = Guid.NewGuid();

        var result = await manager.GenerateWalletAsync(
            new WalletGenerateRequest { ChainType = "Algorand" }, userId);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.UserId.Should().Be(userId);
        result.Result.WalletType.Should().Be(WalletType.Platform);
        result.Result.EncryptedPrivateKey.Should().NotBeNullOrWhiteSpace();
        walletStore.Verify(
            store => store.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Wallet_generation_still_proceeds_on_a_simulated_development_host()
    {
        var walletStore = GivenAnEmptyWalletStore();
        var manager = WalletManagerUnder(
            Configuration(custodyMode: "DevelopmentOnly", blockchainMode: "Simulated"),
            EnvironmentNamed(Environments.Development),
            walletStore);

        var result = await manager.GenerateWalletAsync(
            new WalletGenerateRequest { ChainType = "Algorand" }, Guid.NewGuid());

        result.IsError.Should().BeFalse(result.Message);
    }

    // ─── Fixtures ────────────────────────────────────────────────────────────

    private static IConfiguration Configuration(
        string? custodyMode,
        string blockchainMode,
        string? encryptionKey = UsableEncryptionKey)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CustodyModeResolver.CustodyModeConfigurationPath] = custodyMode,
                [CustodyModeResolver.BlockchainModeConfigurationPath] = blockchainMode,
                [CustodyModeResolver.WalletEncryptionKeyConfigurationPath] = encryptionKey
            })
            .Build();

    private static IHostEnvironment EnvironmentNamed(string environmentName)
        => Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == environmentName);

    private static Mock<IWalletStore> GivenAnEmptyWalletStore()
    {
        var walletStore = new Mock<IWalletStore>();
        walletStore
            .Setup(store => store.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<IWallet>>.Success(Array.Empty<IWallet>()));
        walletStore
            .Setup(store => store.UpsertAsync(It.IsAny<IWallet>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IWallet wallet, CancellationToken _) => AZOAResult<IWallet>.Success(wallet));
        return walletStore;
    }

    private static WalletManager WalletManagerUnder(
        IConfiguration configuration,
        IHostEnvironment environment,
        Mock<IWalletStore> walletStore)
        => new(
            walletStore.Object,
            new Mock<IHolonStore>().Object,
            new Mock<IBlockchainProviderFactory>().Object,
            new WalletKeyService(KeyServiceConfiguration()),
            configuration,
            environment);

    /// <summary>The key service needs a secret of its own; custody availability is the subject here.</summary>
    private static IConfiguration KeyServiceConfiguration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CustodyModeResolver.WalletEncryptionKeyConfigurationPath] = UsableEncryptionKey
            })
            .Build();
}
