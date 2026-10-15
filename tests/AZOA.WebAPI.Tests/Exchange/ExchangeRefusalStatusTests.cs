// SPDX-License-Identifier: UNLICENSED
// OI-43 - "we said no" and "we do not know" are different sentences to a person whose money is
// in the balance. A refusal that provably moved nothing must reach a consumer as a DECLINE it
// can unlock the seller's units on, never as the 409 that means "this may already have run".

using System.Numerics;
using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Exchange;

public sealed class ExchangeRefusalStatusTests
{
    private const string Chain = "Algorand";
    private const string PlatformAsset = "1001";
    private const string ProjectAsset = "2002";
    private const string ProjectReference = "project-1";

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    // ── The shipped default: Exchange:BuyBack.Enabled = false ─────────────────

    [Fact]
    public async Task A_buy_back_the_operator_has_not_opened_declines_rather_than_conflicting()
    {
        var policy = BuyBack(enabled: false);

        var allowed = await policy.EnsureSellAllowedAsync(SellEvaluation());

        allowed.IsError.Should().BeTrue();
        allowed.Message.Should().Be(BuyBackLiquidityPolicy.ClosedMessage);
        allowed.Code.Should().Be(
            ConsumerSafeErrorCodes.Declined,
            "the node read the request and said no - nothing was committed, so a consumer "
            + "holding the seller's units must be free to release them");
        allowed.Code.Should().NotBe(AzoaErrorCodes.OperationNotAllowed);
    }

    [Fact]
    public async Task The_rate_policy_carries_the_liquidity_decline_out_unchanged()
    {
        var policy = new TreasuryRateExchangePolicy(new ExchangeOptions(), BuyBack(enabled: false));

        var decision = await policy.EvaluateAsync(SellEvaluation());

        // This is the code ReserveExchangeManager passes straight through to the controller, so
        // the whole shipped chain - disabled buy-back to wire status - turns on it.
        decision.IsError.Should().BeTrue();
        decision.Code.Should().Be(ConsumerSafeErrorCodes.Declined);
        ConsumerSafeErrors.LeaksRailDetail(decision.Message).Should().BeFalse(
            "a sponsor who has never heard of a market reads this sentence");
    }

    [Fact]
    public async Task A_sell_with_no_liquidity_policy_registered_also_declines()
    {
        var policy = new TreasuryRateExchangePolicy(new ExchangeOptions());

        var decision = await policy.EvaluateAsync(SellEvaluation());

        decision.IsError.Should().BeTrue();
        decision.Message.Should().Be(TreasuryRateExchangePolicy.SellUnavailableMessage);
        decision.Code.Should().Be(ConsumerSafeErrorCodes.Declined);
    }

    // ── The wire status a consumer actually reads ─────────────────────────────

    [Fact]
    public async Task A_declined_refusal_is_returned_as_422_and_never_as_409()
    {
        var manager = ManagerRefusing(
            BuyBackLiquidityPolicy.ClosedMessage, ConsumerSafeErrorCodes.Declined);

        var response = await Controller(manager).Exchange(
            Guid.NewGuid(), new ExchangeRequest(), "idem-1", CancellationToken.None);

        var status = response.Result.Should().BeOfType<ObjectResult>().Which;
        status.StatusCode.Should().Be(
            StatusCodes.Status422UnprocessableEntity,
            "422 is the status a consumer may release a seller's locked units on");
        status.StatusCode.Should().NotBe(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Genuine_ambiguity_still_conflicts_so_units_stay_locked()
    {
        var manager = ManagerRefusing(
            "This project is no longer accepting funds.", AzoaErrorCodes.OperationNotAllowed);

        var response = await Controller(manager).Exchange(
            Guid.NewGuid(), new ExchangeRequest(), "idem-2", CancellationToken.None);

        // 409 must keep meaning "this may already have run": widening it would let a consumer
        // unlock units over an outcome the node cannot vouch for.
        response.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>A buy-back policy with nothing behind it: the disabled check is reached first.</summary>
    private static BuyBackLiquidityPolicy BuyBack(bool enabled) => new(
        new BuyBackOptions { Enabled = enabled },
        new Mock<IProjectTreasury>().Object,
        new Mock<IPlatformReserve>().Object,
        new Mock<IBuyBackQueue>().Object);

    private static ExchangeRateEvaluation SellEvaluation() => new(
        Chain,
        ExchangeDirection.Sell,
        ProjectAsset,
        PlatformAsset,
        new BigInteger(500),
        new BigInteger(250),
        DeclaredRate: null,
        Now,
        TenantId: TenantId,
        ProjectReference: ProjectReference,
        SellerUserId: Guid.NewGuid(),
        RequestKey: "sell-1");

    private static IReserveExchangeManager ManagerRefusing(string message, string code)
    {
        var manager = new Mock<IReserveExchangeManager>();
        manager
            .Setup(candidate => candidate.ExchangeAsync(
                It.IsAny<ExchangeRequest>(),
                It.IsAny<ExchangeCallerContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ExchangeReceipt>.FailureWithCode(message, code));
        return manager.Object;
    }

    /// <summary>An API-key principal carrying the exchange scope, with consent already given.</summary>
    private static ExchangeController Controller(IReserveExchangeManager manager)
    {
        var userId = Guid.NewGuid();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("UserId", userId.ToString()),
            new("AuthMethod", "ApiKey"),
            new("ApiKeyId", Guid.NewGuid().ToString()),
            new("scope", ReserveExchangeManager.ExchangeSigningScope),
        };

        var gate = new Mock<ITenantConsentGate>();
        gate.Setup(candidate => candidate.EnsureAllowedAsync(
                It.IsAny<SigningContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });

        return new ExchangeController(manager, gate.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
                },
            },
        };
    }
}
