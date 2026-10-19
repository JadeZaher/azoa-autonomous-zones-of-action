using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AZOA.WebAPI.IntegrationTests.Http.Infrastructure;

/// <summary>Real NFT HTTP host with local vendor/module edges; see Controllers/AGENTS.md.</summary>
public sealed class NftFlowWebApplicationFactory : HttpFlowWebApplicationFactory
{
    public NftFlowWebApplicationFactory() => Nfts = new(() => Services.CreateScope());

    public NftVendorEdge Nfts { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var fees = new Dictionary<string, string?>
        {
            ["NftFees:Policies:0:ChainType"] = "Algorand", ["NftFees:Policies:0:Network"] = "Devnet",
            ["NftFees:Policies:0:Revision"] = "1", ["NftFees:Policies:0:TreasuryAddress"] = "fixturetreasury",
            ["NftFees:Policies:0:MintNativeBaseUnits"] = "1000", ["NftFees:Policies:0:TransferNativeBaseUnits"] = "1000",
            ["NftFees:Policies:0:BurnNativeBaseUnits"] = "1000", ["NftFees:Policies:0:RevokeNativeBaseUnits"] = "1000",
            ["NftFees:Policies:0:RefundNativeBaseUnits"] = "1000",
        };
        foreach (var (key, value) in fees) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(fees));
        builder.ConfigureTestServices(services =>
        {
            services.Configure<PolicySchemeOptions>("TestAuthForward", options => options.ForwardDefaultSelector = context =>
                context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? JwtBearerDefaults.AuthenticationScheme
                    : context.Request.Headers.ContainsKey("X-Api-Key") ? ApiKeyAuthenticationHandler.SchemeName : TestAuthHandler.SchemeName);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.ForwardAuthenticate = null;
                options.ForwardChallenge = null;
            });

            var provider = new Mock<IBlockchainProvider>(MockBehavior.Strict);
            provider.SetupGet(value => value.ChainType).Returns("Algorand");
            provider.SetupGet(value => value.ActiveNetwork).Returns(ChainNetwork.Devnet);
            provider.Setup(value => value.TryGetModule<INftTransactionModule>(out It.Ref<INftTransactionModule?>.IsAny))
                .Returns((out INftTransactionModule? module) => { module = Nfts; return true; });
            provider.Setup(value => value.TryGetModule<INftProtocolCostModule>(out It.Ref<INftProtocolCostModule?>.IsAny))
                .Returns((out INftProtocolCostModule? module) => { module = Nfts; return true; });
            provider.Setup(value => value.ValidateAddressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string address, CancellationToken _) => AZOAResult<bool>.Success(address.StartsWith("fixture", StringComparison.Ordinal)));
            var original = services.Single(descriptor => descriptor.ServiceType == typeof(IBlockchainProviderFactory));
            var createOriginal = original.ImplementationFactory
                ?? throw new InvalidOperationException("The NFT fixture requires the application's provider factory registration.");
            services.Remove(original);
            services.AddSingleton<IBlockchainProviderFactory>(providerServices =>
                new NftFixtureProviderFactory((IBlockchainProviderFactory)createOriginal(providerServices), provider.Object));
        });
    }

    private sealed class NftFixtureProviderFactory(IBlockchainProviderFactory original, IBlockchainProvider nft) : IBlockchainProviderFactory
    {
        public ChainNetwork DefaultNetwork => original.DefaultNetwork;
        public string DefaultRailId => original.DefaultRailId;
        public IReadOnlyCollection<string> RegisteredRailIds => original.RegisteredRailIds;
        public IBlockchainProvider GetProvider(string chainType, ChainNetwork network)
            => chainType == "Algorand" && network == ChainNetwork.Devnet ? nft : original.GetProvider(chainType, network);
        public IBlockchainProvider GetDefaultProvider() => GetProvider(original.DefaultRailId, DefaultNetwork);
        public IEnumerable<IBlockchainProvider> GetAllEnabledProviders()
            => original.GetAllEnabledProviders().Select(provider => provider.ChainType == "Algorand" && provider.ActiveNetwork == ChainNetwork.Devnet ? nft : provider);
        public bool TryGetModule<T>(IBlockchainProvider provider, out T? module) where T : class, IBlockchainProviderModule
            => provider.TryGetModule(out module);
        public IValueRailProvider GetRailProvider(string railId, RailEnvironment environment) => original.GetRailProvider(railId, environment);
        public bool TryGetRailProvider(string railId, RailEnvironment environment, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IValueRailProvider? provider)
            => original.TryGetRailProvider(railId, environment, out provider);
    }
}
