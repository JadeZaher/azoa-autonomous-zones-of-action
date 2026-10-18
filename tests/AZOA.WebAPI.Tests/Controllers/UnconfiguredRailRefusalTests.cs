using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat.OffRamp;

namespace AZOA.WebAPI.Tests.Controllers;

/// <summary>
/// QA M2 / M3: a rail the node does not run answers with a named refusal, never a 500 (Ethereum recovery
/// on a disabled network) or a "try again shortly" (top-up with no payment provider configured).
/// </summary>
public sealed class UnconfiguredRailRefusalTests
{
    // Shipped appsettings.json: Ethereum is registered but every network is disabled, exactly like the QA node.
    private static readonly IConfiguration ShippedConfiguration = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false)
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Blockchain:Mode"] = "Live" })
        .Build();

    private static BlockchainProviderFactory ShippedFactory() => new(
        [new BlockchainProviderRegistration("Ethereum", () => throw new InvalidOperationException("a disabled network must not construct a provider"))],
        ShippedConfiguration);

    private static ControllerContext SignedIn() => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Test")),
        },
    };

    [Fact]
    public async Task Operator_Ethereum_recovery_list_on_a_disabled_network_is_a_named_refusal()
    {
        var controller = new EthereumOperatorRecoveryController(ShippedFactory()) { ControllerContext = SignedIn() };

        var response = await controller.List(ChainNetwork.Devnet, CancellationToken.None);

        response.Should().BeOfType<ConflictObjectResult>()
            .Which.Value.Should().BeOfType<AZOAResult<bool>>()
            .Which.Code.Should().Be(EthereumRecoveryResolution.UnavailableCode);
    }

    [Fact]
    public async Task User_Ethereum_recovery_list_on_a_disabled_network_is_a_named_refusal()
    {
        var controller = new EthereumRecoveryController(ShippedFactory(), Mock.Of<AZOA.WebAPI.Interfaces.Stores.IWalletStore>()) { ControllerContext = SignedIn() };

        var response = await controller.List(ChainNetwork.Devnet, CancellationToken.None);

        response.Should().BeOfType<ConflictObjectResult>()
            .Which.Value.Should().BeOfType<AZOAResult<bool>>()
            .Which.Code.Should().Be(EthereumRecoveryResolution.UnavailableCode);
    }

    [Fact]
    public async Task Tenant_top_up_on_a_node_without_a_payment_provider_says_not_configured()
    {
        var controller = new TenantPayoutBalanceController(Mock.Of<ITenantPayoutFunding>(), ShippedFactory(), new FiatOptions())
        {
            ControllerContext = SignedIn(),
        };

        var response = await controller.TopUp(new TenantPayoutTopUpRequest { RequestKey = "top-up-1", AmountMinorUnits = 2500 }, CancellationToken.None);

        var refusal = response.Result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        refusal.Value.Should().BeOfType<AZOAResult<FiatCheckoutIntent>>()
            .Which.Code.Should().Be(FiatConsumerErrors.PaymentsNotConfiguredCode);
    }
}
