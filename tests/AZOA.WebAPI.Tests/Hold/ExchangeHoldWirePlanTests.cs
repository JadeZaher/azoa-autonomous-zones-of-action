using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Hold;

/// <summary>Each exchange shape signs exactly its own ordered legs; the reserve always pays the pooled fee.</summary>
public sealed class ExchangeHoldWirePlanTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Participant = Guid.NewGuid();
    private static readonly HoldAssetIdentity Asset = new("Algorand", ChainNetwork.Devnet, "genesis", "42", "RESERVE");
    private static readonly SigningWalletWitness Wallet = new(Guid.NewGuid(), Participant, "PARTICIPANT", "Algorand");

    [Theory]
    [InlineData("Hold", "7:10:PARTICIPANT>TREASURY:Participant", false)]
    [InlineData("Issue", "7:10:PARTICIPANT>TREASURY:Participant|42:400:RESERVE>PARTICIPANT:Reserve", false)]
    [InlineData("Return", "7:10:TREASURY>PARTICIPANT:Treasury|42:400:PARTICIPANT>RESERVE:Participant", true)]
    public void EachShapeProducesItsExactOrderedLegs(string kind, string expected, bool treasurySigns)
    {
        var intent = Intent(kind);
        var plan = ExchangeHoldWirePlan.Create(ExchangeHoldSourceIdentity.ForIntent(intent), 0, intent, Authority())!;

        string.Join('|', plan.Legs.Select(leg => $"{leg.AssetId}:{leg.QuantityBaseUnits}:{leg.Sender}>{leg.Recipient}:{leg.SignerRole}"))
            .Should().Be(expected);
        plan.FeePayerAddress.Should().Be("RESERVE");
        plan.FeePayerContext.IsPlatform.Should().BeTrue();
        (plan.Treasury is not null).Should().Be(treasurySigns);
        plan.Legs.Where(leg => leg.SignerRole == "Participant").Should()
            .OnlyContain(leg => !leg.SigningContext.IsPlatform && leg.SigningContext.Scope == AzoaScopes.SwapSign);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    public void AnExchangeWithoutAnAcceptedFeeCapHasNoWire(string cap)
    {
        var intent = Intent("Issue") with { MaximumNetworkFeeBaseUnits = cap };

        ExchangeHoldWirePlan.Create(ExchangeHoldSourceIdentity.ForIntent(intent), 0, intent, Authority()).Should().BeNull();
    }

    private static ExchangeHoldSourceIntent Intent(string kind)
    {
        var opening = kind == "Hold"
            ? new HoldCommandIntent(1, "exchange:x", "Open", "hold", Tenant, Tenant, null, Tenant, Asset,
                new(Asset, "400", "Reserve", Participant, null, "contributors", DateTimeOffset.UtcNow.AddDays(1), false, null))
            : null;
        return new(1, "request", Guid.NewGuid(), Tenant, Participant, "{}", opening, "7", "10", "project", "TREASURY", Wallet, "10000",
            Units: opening is null ? new(kind, Asset, "400", "contributors") : null);
    }

    private static ExchangeParticipantAuthority Authority()
        => new(1, new(1, new SigningCredentialWitness(1, "ApiKey", Tenant, null, null, null, null, Guid.NewGuid(), "hash",
            [AzoaScopes.SwapSign], false), [AzoaScopes.SwapSign], []), Participant, null, Wallet,
            new(Guid.NewGuid(), AzoaScopes.SwapSign, AzoaScopes.SwapSign, DateTimeOffset.UtcNow, null));
}
