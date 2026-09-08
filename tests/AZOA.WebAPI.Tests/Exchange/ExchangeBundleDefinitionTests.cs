// SPDX-License-Identifier: UNLICENSED
// W5.1 — the exchange bundle: where the funds actually go, who pays the chain fee, and what
// the DAG the node records says about the decision it made.

using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Exchange;

public class ExchangeBundleDefinitionTests
{
    private const string Chain = "Algorand";
    private const string PlatformAsset = "1001";
    private const string ProjectAsset = "2002";
    private const string Partition = "investor";
    private const string ReserveAddress = "RESERVEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string TreasuryAddress = "TREASURYAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string SponsorAddress = "SPONSORAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static readonly Guid Sponsor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WalletId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Tenant = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ── Where the money goes ──────────────────────────────────────────────────

    [Fact]
    public void Buy_sends_the_funds_leg_to_the_project_treasury_and_never_to_the_reserve()
    {
        var bundle = ExchangeBundleDefinition.Compose(Inputs());

        bundle.IsError.Should().BeFalse(bundle.Message);
        var funds = bundle.Result!.Group.Legs.Single(leg => leg.Role == ExchangeLegRole.Funds);
        funds.ToAddress.Should().Be(
            TreasuryAddress,
            "a project whose funding never lands on chain has only a database row, which is the "
            + "ledger of record D47 rejected");
        funds.ToAddress.Should().NotBe(ReserveAddress);
        funds.FromAddress.Should().Be(SponsorAddress);
        funds.AssetId.Should().Be(PlatformAsset);
    }

    [Fact]
    public void Buy_without_a_hold_window_moves_the_units_from_the_reserve_to_the_sponsor()
    {
        var bundle = ExchangeBundleDefinition.Compose(Inputs());

        var units = bundle.Result!.Group.Legs.Single(leg => leg.Role == ExchangeLegRole.Units);
        units.FromAddress.Should().Be(ReserveAddress, "the reserve stays the asset's counterparty");
        units.ToAddress.Should().Be(SponsorAddress);
        units.AssetId.Should().Be(ProjectAsset);
        units.SigningContext.IsPlatform.Should().BeTrue();
    }

    [Fact]
    public void Buy_with_a_hold_window_puts_no_units_leg_on_chain()
    {
        var bundle = ExchangeBundleDefinition.Compose(
            Inputs() with { ReleaseNotBefore = DateTimeOffset.UtcNow.AddDays(30), HoldId = "hold-1" });

        bundle.Result!.Group.Legs.Should().ContainSingle()
            .Which.Role.Should().Be(
                ExchangeLegRole.Funds,
                "held units stay in the reserve's own holding account, so nothing moves until release");
    }

    [Fact]
    public void Sell_reverses_both_legs_so_the_reserve_takes_the_units_back()
    {
        var bundle = ExchangeBundleDefinition.Compose(
            Inputs() with { Direction = ExchangeDirection.Sell });

        var funds = bundle.Result!.Group.Legs.Single(leg => leg.Role == ExchangeLegRole.Funds);
        var units = bundle.Result.Group.Legs.Single(leg => leg.Role == ExchangeLegRole.Units);
        funds.FromAddress.Should().Be(TreasuryAddress);
        funds.ToAddress.Should().Be(SponsorAddress);
        units.FromAddress.Should().Be(SponsorAddress);
        units.ToAddress.Should().Be(ReserveAddress);
    }

    // ── Fee pooling ───────────────────────────────────────────────────────────

    [Fact]
    public void A_leg_whose_declared_signer_disagrees_with_its_identity_is_refused()
    {
        var legs = new[]
        {
            // Declared as the node's own leg, but carrying a person's signing identity. Nothing
            // downstream could then tell which key should sign it.
            new ExchangeGroupLeg(
                ExchangeLegRole.Units, ExchangeLegSigner.PlatformReserve, ProjectAsset,
                ReserveAddress, SponsorAddress, new BigInteger(250), RecipientContext()),
        };

        var plan = ExchangeGroupPlan.TryCreate(
            Chain, ChainNetwork.Devnet, "key", legs, ReserveAddress, ReserveContext());

        plan.IsError.Should().BeTrue();
        plan.Message.Should().Contain("declared signer");
    }

    [Fact]
    public void A_sell_pays_out_of_the_project_treasury_under_the_treasury_own_signer()
    {
        var bundle = ExchangeBundleDefinition.Compose(
            Inputs() with { Direction = ExchangeDirection.Sell });

        bundle.Result!.Group.Legs.Single(leg => leg.Role == ExchangeLegRole.Funds).Signer
            .Should().Be(
                ExchangeLegSigner.ProjectTreasury,
                "a treasury key is DERIVED from the platform key, so signing its money with the "
                + "reserve's key would be a transaction the treasury never authorised");
    }

    [Fact]
    public void The_reserve_pools_the_fee_for_every_person_signed_leg()
    {
        var bundle = ExchangeBundleDefinition.Compose(Inputs());

        bundle.Result!.Group.FeePayerAddress.Should().Be(ReserveAddress);
        bundle.Result.Group.FeePayerSigningContext.IsPlatform.Should().BeTrue();
        bundle.Result.Group.UserSignedLegCount.Should().Be(
            1, "the sponsor signs the funds leg and holds no spendable native units to pay for it");
    }

    [Fact]
    public void A_group_whose_fee_payer_is_the_person_signing_is_refused()
    {
        var legs = new[]
        {
            new ExchangeGroupLeg(
                ExchangeLegRole.Funds, ExchangeLegSigner.Recipient, PlatformAsset,
                SponsorAddress, TreasuryAddress, new BigInteger(500), RecipientContext()),
        };

        var plan = ExchangeGroupPlan.TryCreate(
            Chain, ChainNetwork.Devnet, "key", legs, SponsorAddress, ReserveContext());

        plan.IsError.Should().BeTrue(
            "a custodial account holds no spendable native units, so it cannot pay its own fee");
        plan.Message.Should().Contain("fee payer");
    }

    [Fact]
    public void A_group_whose_fee_payer_is_not_the_node_is_refused()
    {
        var legs = new[]
        {
            new ExchangeGroupLeg(
                ExchangeLegRole.Funds, ExchangeLegSigner.Recipient, PlatformAsset,
                SponsorAddress, TreasuryAddress, new BigInteger(500), RecipientContext()),
        };

        var plan = ExchangeGroupPlan.TryCreate(
            Chain, ChainNetwork.Devnet, "key", legs, ReserveAddress, RecipientContext());

        plan.IsError.Should().BeTrue();
        plan.Message.Should().Contain("reserve account");
    }

    [Fact]
    public void A_person_signed_leg_without_a_resolvable_identity_is_refused()
    {
        var legs = new[]
        {
            new ExchangeGroupLeg(
                ExchangeLegRole.Funds, ExchangeLegSigner.Recipient, PlatformAsset,
                SponsorAddress, TreasuryAddress, new BigInteger(500),
                new SigningContext(Sponsor, Guid.Empty, IsPlatform: false)),
        };

        var plan = ExchangeGroupPlan.TryCreate(
            Chain, ChainNetwork.Devnet, "key", legs, ReserveAddress, ReserveContext());

        plan.IsError.Should().BeTrue();
    }

    [Fact]
    public void A_zero_amount_leg_is_refused()
    {
        var legs = new[]
        {
            new ExchangeGroupLeg(
                ExchangeLegRole.Funds, ExchangeLegSigner.Recipient, PlatformAsset,
                SponsorAddress, TreasuryAddress, BigInteger.Zero, RecipientContext()),
        };

        ExchangeGroupPlan
            .TryCreate(Chain, ChainNetwork.Devnet, "key", legs, ReserveAddress, ReserveContext())
            .IsError.Should().BeTrue();
    }

    // ── The DAG the node records ──────────────────────────────────────────────

    [Fact]
    public void The_definition_is_a_draft_that_declares_it_settles_as_one_group()
    {
        var bundle = ExchangeBundleDefinition.Compose(Inputs());

        bundle.Result!.Definition.Status.Should().Be(
            ActionStatus.Draft, "nothing may start a run of an exchange's plan of record");
        bundle.Result.Definition.Metadata[ExchangeBundleDefinition.DispatchMetadataKey]
            .Should().Be(ExchangeBundleDefinition.AtomicGroupDispatch);
        bundle.Result.Definition.Metadata
            .Should().ContainKey(ExchangeBundleDefinition.FundsLegMetadataKey);
    }

    [Fact]
    public void A_hold_window_becomes_a_real_hold_node_carrying_the_same_window()
    {
        var window = DateTimeOffset.UtcNow.AddDays(45);
        var bundle = ExchangeBundleDefinition.Compose(
            Inputs() with { ReleaseNotBefore = window, HoldId = "hold-7" });

        var holdNode = bundle.Result!.Definition.Nodes
            .Single(node => node.NodeType == ActionNodeType.Hold);
        var config = JsonSerializer.Deserialize<HoldNodeConfig>(holdNode.Config)!;

        config.ReleaseNotBefore.Should().Be(window);
        config.HoldId.Should().Be("hold-7");
        config.AssetId.Should().Be(ProjectAsset);
        config.ReservePartition.Should().Be(Partition);
        config.BeneficiaryUserId.Should().Be(
            Sponsor, "the units are held for the sponsor, never for whoever authored the graph");
        config.Amount.Should().Be("250");
    }

    [Fact]
    public void Without_a_hold_window_the_definition_carries_no_hold_node()
    {
        var bundle = ExchangeBundleDefinition.Compose(Inputs());

        bundle.Result!.Definition.Nodes.Should().NotContain(node => node.NodeType == ActionNodeType.Hold);
        bundle.Result.Definition.Nodes.Select(node => node.NodeType)
            .Should().Equal(ActionNodeType.GateCheck, ActionNodeType.Emit);
    }

    [Fact]
    public void The_bundle_carries_the_group_it_will_actually_submit()
    {
        var bundle = ExchangeBundleDefinition.Compose(Inputs());

        bundle.Result!.Bundle.TargetChain.Should().Be(Chain);
        bundle.Result.Bundle.DeploymentConfig.Should().Be(bundle.Result.Group.ToDeploymentConfig());
        bundle.Result.Bundle.IsPublic.Should().BeFalse();
    }

    [Fact]
    public void Two_compositions_of_the_same_exchange_are_identical()
    {
        var first = ExchangeBundleDefinition.Compose(Inputs());
        var second = ExchangeBundleDefinition.Compose(Inputs());

        second.Result!.Definition.Id.Should().Be(
            first.Result!.Definition.Id, "a retried exchange must compose the same plan, not a second one");
        second.Result.Group.GroupIdentity.Should().Be(first.Result.Group.GroupIdentity);
        second.Result.Bundle.Id.Should().Be(first.Result.Bundle.Id);
    }

    [Fact]
    public void Two_different_exchanges_do_not_share_a_group_identity()
    {
        var first = ExchangeBundleDefinition.Compose(Inputs());
        var second = ExchangeBundleDefinition.Compose(
            Inputs() with { PlatformAmount = new BigInteger(999) });

        second.Result!.Group.GroupIdentity.Should().NotBe(first.Result!.Group.GroupIdentity);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static SigningContext RecipientContext() =>
        SigningContext.ForUser(Sponsor, WalletId).ForChain(Chain)
            .ActingAs(Tenant, AzoaScopes.SwapSign, Sponsor);

    private static SigningContext ReserveContext() =>
        SigningContext.PlatformFor(Chain).ActingAs(Tenant, AzoaScopes.SwapSign, Sponsor);

    private static ExchangeBundleInputs Inputs() => new(
        ReceiptReference: "receipt-reference-0001",
        IdempotencyKey: "exchange:key:0001",
        ChainType: Chain,
        Network: ChainNetwork.Devnet,
        Direction: ExchangeDirection.Buy,
        RecipientUserId: Sponsor,
        ProjectReference: "project-alpha",
        ProjectAssetId: ProjectAsset,
        PlatformAssetId: PlatformAsset,
        PlatformAmount: new BigInteger(500),
        ProjectUnitAmount: new BigInteger(250),
        ReservePartition: Partition,
        ReserveAddress: ReserveAddress,
        TreasuryAddress: TreasuryAddress,
        RecipientAddress: SponsorAddress,
        RecipientSigningContext: RecipientContext(),
        ReserveSigningContext: ReserveContext(),
        TreasurySigningContext: ReserveContext(),
        HoldId: null,
        ReleaseNotBefore: null,
        Purpose: "sponsorship",
        PolicyVersion: TreasuryRateExchangePolicy.Version,
        DeclaredRate: null);
}
