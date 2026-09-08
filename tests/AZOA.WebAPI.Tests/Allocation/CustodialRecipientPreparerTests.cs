// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;

namespace AZOA.WebAPI.Tests.Allocation;

/// <summary>
/// W3.3 proofs for the recipient preparer: a never-used custodial account is made able to
/// hold an asset before anything is sent to it, the person's own key authorises that, and
/// every rail refusal is translated before it can reach a caller.
/// </summary>
public class CustodialRecipientPreparerTests
{
    private const string Chain = "Algorand";
    private const string Asset = "77712345";
    private const string Recipient = "RECIPIENTADDRESSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string FeePayer = "RESERVEADDRESSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly Mock<IBlockchainProviderFactory> _providers = new();
    private readonly Mock<IRecipientPreparationModule> _module = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _walletId = Guid.NewGuid();

    public CustodialRecipientPreparerTests()
    {
        var provider = Mock.Of<IBlockchainProvider>();
        _providers.SetupGet(factory => factory.DefaultNetwork).Returns(ChainNetwork.Devnet);
        _providers.Setup(factory => factory.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
            .Returns(provider);

        IRecipientPreparationModule? module = _module.Object;
        _providers
            .Setup(factory => factory.TryGetModule(It.IsAny<IBlockchainProvider>(), out module))
            .Returns(true);

        _module.SetupGet(candidate => candidate.SupportsRecipientPreparation).Returns(true);
    }

    private CustodialRecipientPreparer Build() =>
        new(_providers.Object, NullLogger<CustodialRecipientPreparer>.Instance);

    private CustodialRecipientPreparation Request(SigningContext? recipientContext = null) =>
        new(Chain, Asset, Recipient,
            recipientContext ?? SigningContext.ForUser(_userId, _walletId).ForChain(Chain),
            FeePayer, SigningContext.PlatformFor(Chain));

    private void RailReports(bool canReceive, bool isBlocked = false, string shortfall = "0")
        => _module
            .Setup(module => module.GetRecipientAssetReadinessAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<RecipientAssetReadiness>
            {
                Result = new RecipientAssetReadiness(canReceive, isBlocked, shortfall),
            });

    [Fact]
    public async Task AlreadyAbleToReceive_PreparesNothing()
    {
        RailReports(canReceive: true);
        var result = await Build().PrepareAsync(Request());

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.AlreadyPrepared.Should().BeTrue();
        result.Result.FundedBaseUnits.Should().Be("0");
        _module.Verify(
            module => module.PrepareRecipientForAssetAsync(
                It.IsAny<RecipientPreparationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NeverHeldTheAsset_FundsTheShortfallAndHasTheRecipientAcceptIt()
    {
        RailReports(canReceive: false, shortfall: "202000");
        RecipientPreparationRequest? captured = null;
        _module
            .Setup(module => module.PrepareRecipientForAssetAsync(
                It.IsAny<RecipientPreparationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RecipientPreparationRequest request, CancellationToken _) =>
            {
                captured = request;
                return AZOAResult<RecipientPreparationReceipt>.Success(
                    new RecipientPreparationReceipt("GROUP", "ACCEPTTX", false, request.FundingBaseUnits));
            });

        var result = await Build().PrepareAsync(Request());

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.AlreadyPrepared.Should().BeFalse();
        result.Result.FundedBaseUnits.Should().Be("202000");
        captured.Should().NotBeNull();
        captured!.FundingBaseUnits.Should().Be("202000");
        captured.FeePayerAddress.Should().Be(FeePayer);
        // The person's own key authorises the acceptance; the platform key must not.
        captured.RecipientSigningContext.IsResolvableUserContext.Should().BeTrue();
        captured.RecipientSigningContext.IsPlatform.Should().BeFalse();
    }

    [Fact]
    public async Task WithoutTheRecipientsOwnSigningContext_RefusesRatherThanSigningAsThePlatform()
    {
        RailReports(canReceive: false, shortfall: "202000");

        var result = await Build().PrepareAsync(Request(SigningContext.PlatformFor(Chain)));

        result.IsError.Should().BeTrue();
        _module.Verify(
            module => module.GetRecipientAssetReadinessAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _module.Verify(
            module => module.PrepareRecipientForAssetAsync(
                It.IsAny<RecipientPreparationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RailWithNoPreparationCapability_FailsClosedWithoutNamingTheRail()
    {
        _module.SetupGet(candidate => candidate.SupportsRecipientPreparation).Returns(false);

        var result = await Build().PrepareAsync(Request());

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(ConsumerSafeErrorCodes.RailUnavailable);
        ConsumerSafeErrors.LeaksRailDetail(result.Message).Should().BeFalse();
    }

    [Fact]
    public async Task BlockedHolding_IsANotPermittedRefusalNotANotReadyOne()
    {
        RailReports(canReceive: true, isBlocked: true);

        var result = await Build().PrepareAsync(Request());

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(ConsumerSafeErrorCodes.NotPermitted);
        ConsumerSafeErrors.LeaksRailDetail(result.Message).Should().BeFalse();
    }

    [Fact]
    public async Task RailRefusal_NeverReachesTheCallerVerbatim()
    {
        RailReports(canReceive: false, shortfall: "202000");
        _module
            .Setup(module => module.PrepareRecipientForAssetAsync(
                It.IsAny<RecipientPreparationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<RecipientPreparationReceipt>.Failure(
                "Algorand group-id assignment did not bind every leg of the prepare recipient "
                + "RECIPIENTADDRESSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA for ASA 77712345 group."));

        var result = await Build().PrepareAsync(Request());

        result.IsError.Should().BeTrue();
        result.Message.Should().NotContain("Algorand").And.NotContain("ASA").And.NotContain(Recipient);
        ConsumerSafeErrors.LeaksRailDetail(result.Message).Should().BeFalse();
    }

    [Fact]
    public async Task PendingAcceptance_IsReportedSoTheCallerDoesNotSendIntoAnUnreadyAccount()
    {
        RailReports(canReceive: false, shortfall: "202000");
        _module
            .Setup(module => module.PrepareRecipientForAssetAsync(
                It.IsAny<RecipientPreparationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<RecipientPreparationReceipt>.Success(
                new RecipientPreparationReceipt("GROUP", "ACCEPTTX", PendingConfirmation: true, "202000")));

        var result = await Build().PrepareAsync(Request());

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.PendingConfirmation.Should().BeTrue();
    }
}
