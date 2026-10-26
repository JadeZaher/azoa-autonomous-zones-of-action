using System.Collections.Concurrent;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Services.Signing;
using Moq;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>Exercises real manager admission and authority selection; persistence/finality use separate real-store/provider fixtures.</summary>
public sealed class FungibleAdmissionHarness
{
    public Guid Actor { get; } = Guid.NewGuid();
    public Mock<IValueAccessService> ValueAccess { get; } = new();
    public Mock<IWalletManager> Wallets { get; } = new();
    public Mock<IWalletStore> WalletStore { get; } = new();
    public Mock<IBlockchainProvider> Provider { get; } = new();
    public Mock<IFungibleCreationModule> Module { get; } = new(MockBehavior.Strict);
    public Mock<IPlatformReserve> Reserve { get; } = new();
    public Mock<IFungibleCreationService> Lifecycle { get; } = new();
    public ConcurrentDictionary<string, FungibleCreationCommand> Commands { get; } = new();
    public FungibleTokenManager Manager { get; }
    public IWallet Wallet { get; }

    public FungibleAdmissionHarness(string target = ReserveMintTargets.PlatformReserve, INodeGovernanceGuard? governance = null,
        IPlatformReserve? reserve = null, IBlockchainProviderFactory? providerFactory = null, string walletChainType = "Algorand")
    {
        Wallet = new AZOA.WebAPI.Models.Wallet { Id = Guid.NewGuid(), UserId = Actor, ChainType = walletChainType, Address = "caller-wallet", WalletType = WalletType.Platform };
        WalletStore.Setup(store => store.GetByUserAsync(Actor, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<IEnumerable<IWallet>>.Success([Wallet]));
        WalletStore.Setup(store => store.GetByIdAsync(Wallet.Id, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<IWallet>.Success(Wallet));
        Wallets.Setup(manager => manager.BootstrapWalletAsync(It.IsAny<WalletGenerateRequest>(), Actor, null)).ReturnsAsync(AZOAResult<IWallet>.Success(Wallet));
        ValueAccess.Setup(service => service.RequireValueAccessAsync(Actor, It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<bool>.Success(true));
        Provider.SetupGet(provider => provider.ChainType).Returns("Algorand");
        Provider.Setup(provider => provider.TryGetModule<IFungibleCreationModule>(out It.Ref<IFungibleCreationModule?>.IsAny))
            .Returns((out IFungibleCreationModule? module) => { module = Module.Object; return true; });
        var providers = new Mock<IBlockchainProviderFactory>();
        providers.SetupGet(factory => factory.DefaultNetwork).Returns(ChainNetwork.Devnet);
        providers.Setup(factory => factory.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>())).Returns(Provider.Object);
        Reserve.Setup(service => service.GetAccountAsync("Algorand", It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<ReserveAccount>.Success(new("Algorand", "platform-reserve", true)));
        var store = new Mock<IFungibleCreationStore>();
        store.Setup(item => item.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string id, CancellationToken _) =>
            Commands.TryGetValue(id, out var command) ? new FungibleCreationOperation { Id = id, RequestJson = JsonSerializer.Serialize(command) } : null);
        Lifecycle.Setup(service => service.AdmitAsync(It.IsAny<FungibleCreationCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync((FungibleCreationCommand command, CancellationToken _) =>
        {
            var id = FungibleCreationIdentity.OperationId(command.ActorUserId, command.TenantId, command.RequestKey);
            var prior = Commands.GetOrAdd(id, command);
            return prior.ClientRequestHash != command.ClientRequestHash ? AZOAResult<FungibleCreationView>.Failure("Intent conflict") : AZOAResult<FungibleCreationView>.Success(View(id, prior));
        });
        Lifecycle.Setup(service => service.GetAsync(It.IsAny<string>(), Actor, It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string id, Guid _, Guid? tenant, CancellationToken _) =>
            AZOAResult<FungibleCreationView>.Success(View(id, Commands[id])));
        Manager = new(ValueAccess.Object, Wallets.Object, WalletStore.Object, providerFactory ?? providers.Object, store.Object, Lifecycle.Object,
            reserve ?? Reserve.Object, new ReserveOptions { FungibleMintTarget = target }, governance ?? NodeGovernanceGuard.Unrestricted,
            new SigningAdmissionAuthorityFactory(Mock.Of<IUserStore>(), WalletStore.Object, Mock.Of<ISurrealExecutor>()), Mock.Of<IActionNodeExecutionStore>());
    }

    public FungibleTokenCreateRequest Request(ulong total = 1000, Guid? tenant = null) => new()
    {
        Name = "Project Unit", UnitName = "UNT", Total = total, Decimals = 2, ChainType = "Algorand",
        AuthenticationWitness = new SigningCredentialWitness(1, "Jwt", Actor, tenant, null, DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(20), null, null, tenant is null ? [] : [AzoaScopes.TokenCreateSign], tenant is null,
            TokenUse: tenant is null ? AzoaClaims.TokenUseLogin : AzoaClaims.TokenUseChild),
    };

    public Task<AZOAResult<FungibleTokenResult>> AdmitAsync(FungibleTokenCreateRequest? request = null, string key = "original")
        => Manager.CreateAsync(Actor, request ?? Request(), Actor, key, "unused");

    private static FungibleCreationView View(string id, FungibleCreationCommand command)
        => new(id, 0, "AwaitingConsent", command.Intent.ChainType, command.Intent.Network.ToString(), null, null, null, null,
            command.WalletId, command.WalletAddress, command.WalletProvisioned, command.HolonId);
}
