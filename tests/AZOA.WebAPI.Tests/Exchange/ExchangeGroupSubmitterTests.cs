// SPDX-License-Identifier: UNLICENSED
// L1.5 — the exchange rail: one atomic group, two signers, pooled fees, and the three outcomes
// a rail can leave a group in. The assertions are about what reached the chain and what did
// NOT, because "it never falls back to two transfers" is only true if nothing broadcast twice.

using System.Net;
using System.Numerics;
using System.Text;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Exchange;

public class ExchangeGroupSubmitterTests
{
    private const string Chain = "Algorand";
    private const string PlatformAsset = "1001";
    private const string ProjectAsset = "2002";

    private static readonly Guid Sponsor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WalletId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Tenant = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task A_two_signer_group_is_broadcast_once_and_confirms()
    {
        var rail = new ExchangeRailHarness();
        rail.Algod.ConfirmSubmitted = true;

        var result = await rail.Submitter().SubmitAsync(rail.BuyPlan());

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Confirmed.Should().BeTrue();
        result.Result.Settlement.Should().Be(ExchangeGroupOutcome.Confirmed);
        result.Result.LegReferences.Should().HaveCount(2, "the plan's two legs each get their own reference");
        result.Result.GroupReference.Should().NotBeNullOrWhiteSpace();
        rail.Algod.Broadcasts.Should().ContainSingle(
            "the legs settle in ONE group; two broadcasts would be the partial exchange atomicity prevents");
    }

    [Fact]
    public async Task Every_leg_of_the_group_is_bound_to_one_chain_group_id()
    {
        var rail = new ExchangeRailHarness();
        rail.Algod.ConfirmSubmitted = true;

        await rail.Submitter().SubmitAsync(rail.BuyPlan());

        rail.Signer.Signed.Select(leg => leg.GroupId).Distinct().Should().ContainSingle()
            .Which.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_sponsor_leg_pays_no_fee_and_the_reserve_pools_the_whole_group()
    {
        var rail = new ExchangeRailHarness();
        rail.Algod.ConfirmSubmitted = true;

        await rail.Submitter().SubmitAsync(rail.BuyPlan());

        var sponsorLeg = rail.Signer.Signed.Single(leg => leg.Sender == rail.SponsorAddress);
        var reserveLeg = rail.Signer.Signed.Single(leg => leg.Sender == rail.ReserveAddress);

        sponsorLeg.Fee.Should().Be(
            0UL,
            "a custodial account holds no spendable native units, so a fee on this leg would fail "
            + "the whole group on the sponsorship moment itself");
        reserveLeg.Fee.Should().Be(
            2000UL, "the reserve pools the minimum fee for every transaction in the group");
    }

    [Fact]
    public async Task A_sell_appends_a_reserve_leg_because_no_plan_leg_leaves_the_reserve()
    {
        var rail = new ExchangeRailHarness();
        rail.Algod.ConfirmSubmitted = true;

        var result = await rail.Submitter().SubmitAsync(rail.SellPlan());

        result.IsError.Should().BeFalse(result.Message);
        rail.Signer.Signed.Should().HaveCount(
            3,
            "the treasury and the seller both hold no spendable native units, so the reserve joins "
            + "the group purely to carry its pooled fee");
        rail.Signer.Signed.Single(leg => leg.Sender == rail.ReserveAddress).Fee.Should().Be(3000UL);
        rail.Signer.Signed.Where(leg => leg.Sender != rail.ReserveAddress)
            .Should().OnlyContain(leg => leg.Fee == 0UL);
        result.Result!.LegReferences.Should().HaveCount(
            2, "the appended fee leg is not one of the plan's legs and never poses as one");
    }

    [Fact]
    public async Task A_group_that_expires_unmined_reports_dropped_after_acceptance()
    {
        var rail = new ExchangeRailHarness();
        rail.Algod.ConfirmSubmitted = false;
        rail.Algod.AdvanceRoundsAfterBroadcast = 500;

        var result = await rail.Submitter().SubmitAsync(rail.BuyPlan());

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Confirmed.Should().BeFalse();
        result.Result.DroppedAfterAcceptance.Should().BeTrue(
            "past its validity window a transaction can never be committed, so the exchange above "
            + "may hand every commitment back");
    }

    [Fact]
    public async Task A_group_rejected_by_the_pool_reports_dropped_after_acceptance()
    {
        var rail = new ExchangeRailHarness();
        rail.Algod.PoolError = "overspend";

        var result = await rail.Submitter().SubmitAsync(rail.BuyPlan());

        result.Result!.DroppedAfterAcceptance.Should().BeTrue();
    }

    [Fact]
    public async Task A_group_whose_reads_fail_is_never_reported_dropped()
    {
        var rail = new ExchangeRailHarness();
        rail.Algod.PendingReadsFail = true;
        rail.Algod.AdvanceRoundsAfterBroadcast = 500;

        var result = await rail.Submitter().SubmitAsync(rail.BuyPlan());

        result.Result!.Settlement.Should().Be(
            ExchangeGroupOutcome.Accepted,
            "an incomplete observation of a group the rail already holds is not evidence it "
            + "failed, and unwinding on it would undo an exchange that might settle");
        result.Result.DroppedAfterAcceptance.Should().BeFalse();
    }

    [Fact]
    public async Task An_unresolved_group_reports_accepted_rather_than_confirmed()
    {
        var rail = new ExchangeRailHarness();
        rail.Algod.ConfirmSubmitted = false;

        var result = await rail.Submitter().SubmitAsync(rail.BuyPlan());

        result.Result!.Confirmed.Should().BeFalse();
        result.Result.Settlement.Should().Be(ExchangeGroupOutcome.Accepted);
    }

    [Fact]
    public async Task A_key_that_does_not_hold_the_leg_account_never_signs_and_nothing_is_broadcast()
    {
        var rail = new ExchangeRailHarness();
        // The custody door hands back a key for some other account. The discriminator still says
        // "the sponsor", which is exactly the mis-sign the address check exists to catch.
        rail.SponsorSigningKey = new AlgoAccount().KeyPair.ClearTextPrivateKey;

        var result = await rail.Submitter().SubmitAsync(rail.BuyPlan());

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(ConsumerSafeErrorCodes.RailUnavailable);
        rail.Algod.Broadcasts.Should().BeEmpty("a group that could not be fully signed is never sent");
    }

    [Fact]
    public async Task A_second_submission_under_one_key_is_refused_without_reaching_the_chain()
    {
        var rail = new ExchangeRailHarness();
        rail.Algod.ConfirmSubmitted = true;
        var submitter = rail.Submitter();
        var plan = rail.BuyPlan();

        await submitter.SubmitAsync(plan);
        var second = await submitter.SubmitAsync(plan);

        second.IsError.Should().BeTrue();
        rail.Algod.Broadcasts.Should().ContainSingle();
    }

    [Fact]
    public void A_plan_for_another_rail_is_refused()
    {
        var rail = new ExchangeRailHarness();

        rail.Submitter().CanSubmit("Solana").Should().BeFalse();
        rail.Submitter().CanSubmit("Algorand").Should().BeTrue();
        rail.Algod.Broadcasts.Should().BeEmpty();
    }

    [Fact]
    public async Task The_fallback_submitter_refuses_and_never_sends_the_legs_one_at_a_time()
    {
        var rail = new ExchangeRailHarness();

        var result = await new UnavailableExchangeGroupSubmitter().SubmitAsync(rail.BuyPlan());

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(ConsumerSafeErrorCodes.RailUnavailable);
        rail.Algod.Broadcasts.Should().BeEmpty();
    }

    // ── The harness ───────────────────────────────────────────────────────────

    private sealed class ExchangeRailHarness
    {
        private readonly AlgoAccount _sponsor = new();
        private readonly AlgoAccount _reserve = new();
        private readonly AlgoAccount _treasury = new();

        public ExchangeRailHarness()
        {
            SponsorSigningKey = _sponsor.KeyPair.ClearTextPrivateKey;
            Algod = new StubAlgodHandler();
        }

        public StubAlgodHandler Algod { get; }

        public RecordingAlgorandSigner Signer { get; } = new();

        public byte[] SponsorSigningKey { get; set; }

        public string SponsorAddress => _sponsor.Address.EncodeAsString();

        public string ReserveAddress => _reserve.Address.EncodeAsString();

        public string TreasuryAddress => _treasury.Address.EncodeAsString();

        public AlgorandExchangeGroupSubmitter Submitter()
        {
            var provider = new Mock<IBlockchainProvider>();
            provider.SetupGet(candidate => candidate.ChainType).Returns(Chain);
            provider.SetupGet(candidate => candidate.ActiveNetwork).Returns(ChainNetwork.Devnet);

            var providers = new Mock<IBlockchainProviderFactory>();
            providers
                .Setup(factory => factory.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(provider.Object);

            var custody = new Mock<IKeyCustodyService>();
            custody
                .Setup(seam => seam.WithSigningKeyAsync(
                    It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
                .Returns((SigningContext _, Func<byte[], Task<AZOAResult<byte[]>>> sign)
                    => RunAsync(sign, SponsorSigningKey));

            var reserve = new Mock<IPlatformReserve>();
            reserve
                .Setup(seam => seam.SignAsReserveAsync(
                    It.IsAny<string>(), It.IsAny<SigningContext>(),
                    It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
                .Returns((string _, SigningContext __, Func<byte[], Task<AZOAResult<byte[]>>> sign)
                    => RunAsync(sign, _reserve.KeyPair.ClearTextPrivateKey));

            var treasury = new Mock<IProjectTreasury>();
            treasury
                .Setup(seam => seam.SignAsTreasuryAsync(
                    It.IsAny<ProjectTreasuryReference>(), It.IsAny<SigningContext>(),
                    It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
                .Returns((ProjectTreasuryReference _, SigningContext __, Func<byte[], Task<AZOAResult<byte[]>>> sign)
                    => RunAsync(sign, _treasury.KeyPair.ClearTextPrivateKey));

            return new AlgorandExchangeGroupSubmitter(
                providers.Object,
                custody.Object,
                reserve.Object,
                treasury.Object,
                new RecordingSignerFactory(Signer),
                new StubHttpClientFactory(Algod),
                Configuration(),
                new AlgorandExchangeGroupSubmitterOptions
                {
                    ValidityWindowRounds = 25,
                    ConfirmationPollAttempts = 2,
                    ConfirmationPollDelayMilliseconds = 1,
                    MinimumFeeMicroAlgos = 1000,
                });
        }

        /// <summary>A buy: the sponsor pays the project, the reserve delivers the units.</summary>
        public ExchangeGroupPlan BuyPlan()
        {
            var legs = new[]
            {
                new ExchangeGroupLeg(
                    ExchangeLegRole.Funds, ExchangeLegSigner.Recipient, PlatformAsset,
                    SponsorAddress, TreasuryAddress, new BigInteger(500), RecipientContext()),
                new ExchangeGroupLeg(
                    ExchangeLegRole.Units, ExchangeLegSigner.PlatformReserve, ProjectAsset,
                    ReserveAddress, SponsorAddress, new BigInteger(250), PlatformContext()),
            };

            return Plan(legs);
        }

        /// <summary>A sell: the project's treasury pays, the seller returns units to the reserve.</summary>
        public ExchangeGroupPlan SellPlan()
        {
            var legs = new[]
            {
                new ExchangeGroupLeg(
                    ExchangeLegRole.Funds, ExchangeLegSigner.ProjectTreasury, PlatformAsset,
                    TreasuryAddress, SponsorAddress, new BigInteger(500), PlatformContext()),
                new ExchangeGroupLeg(
                    ExchangeLegRole.Units, ExchangeLegSigner.Recipient, ProjectAsset,
                    SponsorAddress, ReserveAddress, new BigInteger(250), RecipientContext()),
            };

            return Plan(legs);
        }

        private ExchangeGroupPlan Plan(IReadOnlyList<ExchangeGroupLeg> legs)
        {
            var plan = ExchangeGroupPlan.TryCreate(
                Chain,
                ChainNetwork.Devnet,
                $"exchange:{Guid.NewGuid():N}",
                legs,
                ReserveAddress,
                PlatformContext(),
                new ProjectTreasuryReference(Chain, Tenant, "project-alpha"));

            plan.IsError.Should().BeFalse(plan.Message);
            return plan.Result!;
        }

        private static SigningContext RecipientContext() =>
            SigningContext.ForUser(Sponsor, WalletId).ForChain(Chain)
                .ActingAs(Tenant, AzoaScopes.SwapSign, Sponsor);

        private static SigningContext PlatformContext() =>
            SigningContext.PlatformFor(Chain).ActingAs(Tenant, AzoaScopes.SwapSign, Sponsor);

        private static IConfiguration Configuration() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Blockchain:Chains:0:ChainType"] = Chain,
                    ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://algod.invalid",
                    ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                    ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "5000",
                })
                .Build();

        private static async Task<AZOAResult<AZOAResult<byte[]>>> RunAsync(
            Func<byte[], Task<AZOAResult<byte[]>>> sign, byte[] key)
            => AZOAResult<AZOAResult<byte[]>>.Success(await sign(key));
    }
}

// NOTE: "a sell is admitted only when the liquidity policy admits it" is already asserted,
// unduplicated, in ExchangeRatePolicyTests.cs:27,40,51 — the registration OI-62 asked for
// landed before this wave (Program.cs, AddBuyBackLiquidityPolicy).

/// <summary>What one leg looked like at the moment it was handed to the signer.</summary>
internal sealed record SignedExchangeLeg(string Sender, ulong Fee, string? GroupId);

/// <summary>
/// The real Algorand signer, with the canonical transaction decoded on the way past so a test
/// can assert what the chain would have been asked to do.
/// </summary>
internal sealed class RecordingAlgorandSigner : ITransactionSigner
{
    private readonly AlgorandTransactionSigner _inner = new();

    public string ChainType => "Algorand";

    public List<SignedExchangeLeg> Signed { get; } = new();

    public AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key)
    {
        var transaction = Algorand.Utils.Encoder.DecodeFromMsgPack<Transaction>(canonicalTxn);
        Signed.Add(new SignedExchangeLeg(
            transaction.Sender.EncodeAsString(),
            Convert.ToUInt64(transaction.Fee),
            transaction.Group?.ToString()));
        return _inner.Sign(canonicalTxn, key);
    }
}

internal sealed class RecordingSignerFactory : ITransactionSignerFactory
{
    private readonly ITransactionSigner _signer;

    public RecordingSignerFactory(ITransactionSigner signer) => _signer = signer;

    public ITransactionSigner GetSigner(string chainType) => _signer;

    public bool TryGetSigner(string chainType, out ITransactionSigner? signer)
    {
        signer = string.Equals(chainType, _signer.ChainType, StringComparison.OrdinalIgnoreCase)
            ? _signer
            : null;
        return signer is not null;
    }
}

internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>An algod that answers the three calls the submitter makes, and records the one that matters.</summary>
internal sealed class StubAlgodHandler : HttpMessageHandler
{
    private long _round = 1000;

    /// <summary>Every broadcast payload. More than one entry means the legs were not sent as a group.</summary>
    public List<byte[]> Broadcasts { get; } = new();

    /// <summary>Whether a read-back reports the group committed.</summary>
    public bool ConfirmSubmitted { get; set; }

    /// <summary>Non-empty means the pool rejected the group outright.</summary>
    public string? PoolError { get; set; }

    /// <summary>Makes every read-back fail, so no observation is ever complete.</summary>
    public bool PendingReadsFail { get; set; }

    /// <summary>Rounds the chain moves on the moment the group is broadcast.</summary>
    public long AdvanceRoundsAfterBroadcast { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;

        if (path == "/v2/transactions" && request.Method == HttpMethod.Post)
        {
            Broadcasts.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            _round += AdvanceRoundsAfterBroadcast;
            return Json("{\"txId\":\"STUBTXID\"}");
        }

        if (path == "/v2/transactions/params")
        {
            return Json(
                "{\"fee\":0,\"min-fee\":1000,\"last-round\":" + _round
                + ",\"genesis-id\":\"testnet-v1.0\","
                + "\"genesis-hash\":\"SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=\"}");
        }

        if (path.StartsWith("/v2/transactions/pending/", StringComparison.Ordinal))
        {
            if (PendingReadsFail)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            if (!string.IsNullOrWhiteSpace(PoolError))
                return Json("{\"confirmed-round\":0,\"pool-error\":\"" + PoolError + "\"}");
            return ConfirmSubmitted
                ? Json("{\"confirmed-round\":" + _round + "}")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };
}
