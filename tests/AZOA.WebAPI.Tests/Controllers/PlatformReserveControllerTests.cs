// SPDX-License-Identifier: UNLICENSED
//
// OI-22. These tests assert at the COUNTER, not at the doorway: every refusal is checked
// against the ledger row the declaration would have carved, so a route that answered 403
// while still widening a budget would fail here.

using System.Numerics;
using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AZOA.WebAPI.Tests.Controllers;

public sealed class PlatformReserveControllerTests
{
    private const string Chain = "Algorand";
    private const string Network = "Devnet";
    private const string AssetId = "555";
    private const string AssetTotalSupply = "1000";
    private const string ContributorPartition = "contributor";
    private const string InvestorPartition = "investor";

    // ── Who may carve a budget ────────────────────────────────────────────────

    [Fact]
    public async Task Declare_PlainSignedInUser_IsForbiddenAndCarvesNothing()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        var controller = harness.ControllerFor(SignedInUser(owner));

        var response = await controller.Declare(
            AssetId, Chain, Budget((ContributorPartition, "400")), CancellationToken.None);

        Forbidden(response);
        harness.DeclaredFor(ContributorPartition).Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task Declare_ApiKeyWithoutTheTokenCreateScope_IsForbiddenAndCarvesNothing()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        var controller = harness.ControllerFor(
            ApiKeyPrincipal(owner, AzoaScopes.TransferSign));

        var response = await controller.Declare(
            AssetId, Chain, Budget((ContributorPartition, "400")), CancellationToken.None);

        Forbidden(response);
        harness.DeclaredFor(ContributorPartition).Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task Declare_OwningTenant_CarvesThePartition()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        var controller = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));

        var response = await controller.Declare(
            AssetId, Chain, Budget((ContributorPartition, "400")), CancellationToken.None);

        var view = Succeeded(response);
        view.Partitions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Name = ContributorPartition,
                Declared = "400",
                Available = "400",
            });
        harness.DeclaredFor(ContributorPartition).Should().Be(new BigInteger(400));
    }

    [Fact]
    public async Task Declare_AnotherTenantsAsset_IsForbiddenAndLeavesTheOwnersBudgetWhereItWas()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        var ownerController = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));
        await ownerController.Declare(
            AssetId, Chain, Budget((ContributorPartition, "400")), CancellationToken.None);

        var intruder = harness.ControllerFor(
            ApiKeyPrincipal(Guid.NewGuid(), AzoaScopes.TokenCreateSign));
        var response = await intruder.Declare(
            AssetId, Chain, Budget((ContributorPartition, "900")), CancellationToken.None);

        Forbidden(response);
        harness.DeclaredFor(ContributorPartition).Should().Be(new BigInteger(400));
    }

    [Fact]
    public async Task Declare_PlatformUnit_RefusesATenantUnderTheDefaultPolicy()
    {
        // The node's own unit carries no tenant on its ledger row, and the default
        // PlatformUnitDrawPolicy keeps it to the operator's own rails (OI-21).
        var harness = ReserveRouteHarness.WithPlatformUnit();
        var controller = harness.ControllerFor(
            ApiKeyPrincipal(Guid.NewGuid(), AzoaScopes.TokenCreateSign));

        var response = await controller.Declare(
            AssetId, Chain, Budget((ContributorPartition, "400")), CancellationToken.None);

        Forbidden(response);
        harness.DeclaredFor(ContributorPartition).Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task Declare_PlatformUnit_AdmitsTheNodeOperator()
    {
        var harness = ReserveRouteHarness.WithPlatformUnit();
        var controller = harness.ControllerFor(NodeOperatorPrincipal());

        var response = await controller.Declare(
            AssetId, Chain, Budget((ContributorPartition, "400")), CancellationToken.None);

        Succeeded(response);
        harness.DeclaredFor(ContributorPartition).Should().Be(new BigInteger(400));
    }

    [Fact]
    public async Task Declare_TenantRegisteredAsset_RefusesTheNodeOperator()
    {
        // The draw side treats an operator-signalled move on a tenant's asset as a miswired
        // seam rather than authority; the declaration side must not be looser.
        var harness = ReserveRouteHarness.WithTenantAsset(out _);
        var controller = harness.ControllerFor(NodeOperatorPrincipal());

        var response = await controller.Declare(
            AssetId, Chain, Budget((ContributorPartition, "400")), CancellationToken.None);

        Forbidden(response);
        harness.DeclaredFor(ContributorPartition).Should().Be(BigInteger.Zero);
    }

    // ── What a declaration may say ────────────────────────────────────────────

    [Fact]
    public async Task Declare_TheSameBudgetTwice_DoesNotDoubleIt()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        var controller = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));

        await controller.Declare(
            AssetId, Chain, Budget((ContributorPartition, "400")), CancellationToken.None);
        var second = await controller.Declare(
            AssetId, Chain, Budget((ContributorPartition, "400")), CancellationToken.None);

        Succeeded(second);
        harness.DeclaredFor(ContributorPartition).Should().Be(new BigInteger(400));
    }

    [Fact]
    public async Task Declare_MoreThanTheSupply_IsRefusedAndCarvesNothing()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        var controller = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));

        var response = await controller.Declare(
            AssetId,
            Chain,
            Budget((ContributorPartition, "600"), (InvestorPartition, "600")),
            CancellationToken.None);

        BadRequest(response).Message.Should().Contain("total supply");
        harness.DeclaredFor(ContributorPartition).Should().Be(BigInteger.Zero);
        harness.DeclaredFor(InvestorPartition).Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task Declare_TwoSpellingsOfOnePartition_IsRefusedRatherThanSilentlyKeepingOne()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        var controller = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));

        var response = await controller.Declare(
            AssetId,
            Chain,
            Budget(("Contributor", "400"), ("contributor", "900")),
            CancellationToken.None);

        BadRequest(response).Message.Should().Contain("more than once");
        harness.DeclaredFor(ContributorPartition).Should().Be(BigInteger.Zero);
    }

    [Theory]
    [InlineData("-400")]
    [InlineData("4 00")]
    [InlineData("400.5")]
    [InlineData("")]
    public async Task Declare_ABudgetThatIsNotAWholeNonNegativeCount_IsRefused(string budget)
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        var controller = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));

        var response = await controller.Declare(
            AssetId, Chain, Budget((ContributorPartition, budget)), CancellationToken.None);

        BadRequest(response);
        harness.DeclaredFor(ContributorPartition).Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task Declare_NoPartitionsAtAll_IsRefused()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        var controller = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));

        var response = await controller.Declare(
            AssetId, Chain, new ReservePartitionDeclarationBody(), CancellationToken.None);

        BadRequest(response).Message.Should().Contain("at least one partition");
    }

    [Fact]
    public async Task Declare_AnAssetWithNoReserveLedgerRow_IsNotFound()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        var controller = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));

        var response = await controller.Declare(
            "no-such-asset", Chain, Budget((ContributorPartition, "400")), CancellationToken.None);

        response.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    // ── Which ledger row a request addresses ──────────────────────────────────

    [Fact]
    public async Task Declare_QueryAndBodyNamingDifferentRails_IsRefused()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        var controller = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));

        var body = Budget((ContributorPartition, "400"));
        body.Chain = "Solana";

        var response = await controller.Declare(AssetId, Chain, body, CancellationToken.None);

        BadRequest(response).Message.Should().Contain("two different rails");
        harness.DeclaredFor(ContributorPartition).Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task Declare_NoRailNamedAndOneApprovedChain_ResolvesItFromConfiguration()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        harness.Options.Chains.Add(Chain);
        var controller = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));

        var response = await controller.Declare(
            AssetId, chain: null, Budget((ContributorPartition, "400")), CancellationToken.None);

        Succeeded(response);
        harness.DeclaredFor(ContributorPartition).Should().Be(new BigInteger(400));
    }

    [Fact]
    public async Task Declare_NoRailNamedAndNoSingleApprovedChain_IsRefusedRatherThanGuessed()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);

        // Two DECLARED rails is the only way this node is genuinely ambiguous: OI-74 made the
        // door read EffectiveChains, which falls back to the single shipped chain, so declaring
        // none resolves rather than refuses. That fallback is pinned by the test below.
        harness.Options.Chains.Add(Chain);
        harness.Options.Chains.Add("Solana");

        var controller = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));

        var response = await controller.Declare(
            AssetId, chain: null, Budget((ContributorPartition, "400")), CancellationToken.None);

        BadRequest(response).Message.Should().Contain("must name the rail");
        harness.DeclaredFor(ContributorPartition).Should().Be(BigInteger.Zero);
    }

    [Fact]
    public async Task Declare_NoRailNamedAndNoChainDeclared_ResolvesTheOneChainTheCounterPermits()
    {
        // OI-74's actual contract, which the change shipped without a test: on a node that
        // declared no chains this door must choose the same rail PlatformReserveService.PermitsChain
        // would accept. If the two ever disagree, a declaration lands on a ledger row the counter
        // then refuses to draw -- budget recorded, nothing drawable.
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        harness.Options.Chains.Should().BeEmpty("this test is about the undeclared node");

        var controller = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));

        var response = await controller.Declare(
            AssetId, chain: null, Budget((ContributorPartition, "400")), CancellationToken.None);

        Succeeded(response);
        harness.DeclaredFor(ContributorPartition).Should().Be(new BigInteger(400));
        harness.Options.PermitsChain(ReserveOptions.ShippedChains[0])
            .Should().BeTrue("the door resolved the rail the counter must also permit");
    }

    // ── The valve itself: a declaration is what opens the draw ────────────────

    [Fact]
    public async Task Declare_IsWhatTurnsAnUndrawablePartitionIntoADrawableOne()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);

        var beforeDeclaring = await harness.IssueAsync(owner, ContributorPartition, 100, "draw-1");
        beforeDeclaring.IsError.Should().BeTrue("an undeclared partition fails closed");
        beforeDeclaring.Message.Should().Contain("not declared");

        var controller = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));
        Succeeded(await controller.Declare(
            AssetId, Chain, Budget((ContributorPartition, "400")), CancellationToken.None));

        var afterDeclaring = await harness.IssueAsync(owner, ContributorPartition, 100, "draw-2");
        afterDeclaring.IsError.Should().BeFalse(afterDeclaring.Message);
        afterDeclaring.Result!.Counters.Issued.Should().Be(new BigInteger(100));
    }

    // ── Reading a budget back ─────────────────────────────────────────────────

    [Fact]
    public async Task Get_OwningTenant_SeesWhatItCarved()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        var controller = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));
        await controller.Declare(
            AssetId, Chain, Budget((ContributorPartition, "400")), CancellationToken.None);

        var response = await controller.Get(AssetId, Chain, CancellationToken.None);

        var view = Succeeded(response);
        view.AssetId.Should().Be(AssetId);
        view.Partitions.Should().ContainSingle().Which.Declared.Should().Be("400");
    }

    [Fact]
    public async Task Get_AnotherTenantsAsset_IsForbidden()
    {
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        var ownerController = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));
        await ownerController.Declare(
            AssetId, Chain, Budget((ContributorPartition, "400")), CancellationToken.None);

        var intruder = harness.ControllerFor(
            ApiKeyPrincipal(Guid.NewGuid(), AzoaScopes.TokenCreateSign));

        var response = await intruder.Get(AssetId, Chain, CancellationToken.None);

        Forbidden(response);
    }

    [Fact]
    public async Task Get_NoReserveAddressIsEverProjected()
    {
        // The view is a budget, not an account. A reserve address on this wire shape would
        // put the node's custodial account in front of every tenant integration.
        var harness = ReserveRouteHarness.WithTenantAsset(out var owner);
        var controller = harness.ControllerFor(ApiKeyPrincipal(owner, AzoaScopes.TokenCreateSign));

        var response = await controller.Get(AssetId, Chain, CancellationToken.None);

        Succeeded(response);
        typeof(ReservePartitionsView).GetProperties()
            .Select(property => property.Name)
            .Should().NotContain(name => name.Contains("Address", StringComparison.OrdinalIgnoreCase));
    }

    // ── Principals ────────────────────────────────────────────────────────────

    private static Claim[] SignedInUser(Guid userId) =>
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("UserId", userId.ToString()),
        };

    private static Claim[] ApiKeyPrincipal(Guid tenantId, params string[] scopes) =>
        new[]
        {
            new Claim("AuthMethod", "ApiKey"),
            new Claim(ClaimTypes.NameIdentifier, tenantId.ToString()),
            new Claim("UserId", tenantId.ToString()),
            new Claim("ApiKeyId", Guid.NewGuid().ToString()),
        }
        .Concat(scopes.Select(scope => new Claim("scope", scope)))
        .ToArray();

    private static Claim[] NodeOperatorPrincipal() =>
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(AzoaClaims.TokenUse, AzoaClaims.TokenUseNodeOperator),
            new Claim("scope", AzoaScopes.Operator),
        };

    // ── Request + response helpers ────────────────────────────────────────────

    private static ReservePartitionDeclarationBody Budget(params (string Name, string Declared)[] partitions) =>
        new()
        {
            Partitions = partitions.ToDictionary(
                partition => partition.Name, partition => partition.Declared, StringComparer.Ordinal),
        };

    private static ReservePartitionsView Succeeded(
        ActionResult<AZOAResult<ReservePartitionsView>> response)
    {
        var payload = response.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<AZOAResult<ReservePartitionsView>>().Subject;
        payload.IsError.Should().BeFalse(payload.Message);
        return payload.Result!;
    }

    private static AZOAResult<ReservePartitionsView> Forbidden(
        ActionResult<AZOAResult<ReservePartitionsView>> response)
    {
        var result = response.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var payload = result.Value.Should().BeOfType<AZOAResult<ReservePartitionsView>>().Subject;
        payload.IsError.Should().BeTrue();
        payload.Code.Should().Be(AzoaErrorCodes.Forbidden);
        return payload;
    }

    private static AZOAResult<ReservePartitionsView> BadRequest(
        ActionResult<AZOAResult<ReservePartitionsView>> response)
    {
        var payload = response.Result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().BeOfType<AZOAResult<ReservePartitionsView>>().Subject;
        payload.IsError.Should().BeTrue();
        return payload;
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A real <see cref="PlatformReserveService"/> over an in-memory ledger, so an assertion
    /// can read the persisted counters rather than the response the route composed.
    /// </summary>
    private sealed class ReserveRouteHarness
    {
        private readonly InMemoryLedgerStore _ledger;
        private readonly IPlatformReserve _reserve;

        private ReserveRouteHarness(InMemoryLedgerStore ledger, IPlatformReserve reserve, ReserveOptions options)
        {
            _ledger = ledger;
            _reserve = reserve;
            Options = options;
        }

        public ReserveOptions Options { get; }

        /// <summary>An asset a tenant registered; that tenant owns its partitions.</summary>
        public static ReserveRouteHarness WithTenantAsset(out Guid owner)
        {
            owner = Guid.NewGuid();
            return Build(owner);
        }

        /// <summary>The node's own unit: a ledger row with no tenant on it.</summary>
        public static ReserveRouteHarness WithPlatformUnit() => Build(null);

        private static ReserveRouteHarness Build(Guid? tenantId)
        {
            var ledger = new InMemoryLedgerStore();
            ledger.Seed(NewRow(tenantId));

            var providers = new Mock<IBlockchainProviderFactory>();
            providers.SetupGet(factory => factory.DefaultNetwork).Returns(ChainNetwork.Devnet);

            var options = new ReserveOptions();
            var reserve = new PlatformReserveService(
                Mock.Of<IKeyCustodyService>(), providers.Object, ledger, options);

            return new ReserveRouteHarness(ledger, reserve, options);
        }

        public PlatformReserveController ControllerFor(params Claim[] claims) =>
            new(_reserve, Options)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
                    },
                },
            };

        /// <summary>What the LEDGER says the partition is declared at — the counter itself.</summary>
        public BigInteger DeclaredFor(string partition)
        {
            var row = _ledger.Row(PlatformReserveLedger.RecordIdFor(Chain, Network, AssetId));
            var partitions = ReservePartitionCodec.Decode(row.Partitions, out var error);
            error.Should().BeEmpty();
            return partitions!.TryGetValue(ReservePartitionCodec.Normalize(partition), out var counters)
                ? counters.Declared
                : BigInteger.Zero;
        }

        /// <summary>Draws the partition as the owning tenant would, to prove the rail opened.</summary>
        public Task<AZOAResult<ReserveDrawOutcome>> IssueAsync(
            Guid actingTenantId, string partition, long amount, string operationKey) =>
            _reserve.DrawAsync(new ReserveDrawRequest(
                Chain,
                AssetId,
                partition,
                ReserveDrawKind.Issue,
                new BigInteger(amount),
                operationKey,
                ReserveDrawAuthority.ForTenant(actingTenantId)));

        private static PlatformReserveLedger NewRow(Guid? tenantId) =>
            new()
            {
                Id = PlatformReserveLedger.RecordIdFor(Chain, Network, AssetId),
                AssetId = AssetId,
                Chain = Chain,
                Network = Network,
                ReserveAddress = "RESERVE-ADDRESS",
                TenantId = tenantId is { } tenant && tenant != Guid.Empty ? tenant.ToString("N") : null,
                UnitName = "UNIT",
                Decimals = 2,
                TotalSupply = AssetTotalSupply,
                Partitions = ReservePartitionCodec.Encode(
                    new Dictionary<string, ReservePartitionCounters>()),
                RecentDrawKeys = Array.Empty<string>(),
                Version = 0,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
    }

    /// <summary>
    /// In-memory reserve ledger with the production store's compare-and-swap contract. Rows
    /// are cloned in and out so the service mutating the row it read cannot reach the
    /// "persisted" copy without going through a version-checked write.
    /// </summary>
    private sealed class InMemoryLedgerStore : IPlatformReserveLedgerStore
    {
        private readonly Dictionary<string, PlatformReserveLedger> _rows = new(StringComparer.Ordinal);

        public void Seed(PlatformReserveLedger row) => _rows[row.Id] = Clone(row);

        public PlatformReserveLedger Row(string recordId) => _rows[recordId];

        public Task<AZOAResult<PlatformReserveLedger?>> GetAsync(
            string recordId, CancellationToken ct = default)
        {
            PlatformReserveLedger? row = null;
            if (_rows.TryGetValue(recordId, out var stored))
                row = Clone(stored);

            return Task.FromResult(new AZOAResult<PlatformReserveLedger?>
            {
                Result = row,
                Message = row is null ? "Not found." : "Success",
            });
        }

        public Task<AZOAResult<PlatformReserveLedgerClaim>> CreateIfAbsentAsync(
            PlatformReserveLedger row, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(row.Id, out var existing))
                return Task.FromResult(AZOAResult<PlatformReserveLedgerClaim>.Success(
                    new PlatformReserveLedgerClaim(Clone(existing), Created: false), "Already registered."));

            _rows[row.Id] = Clone(row);
            return Task.FromResult(AZOAResult<PlatformReserveLedgerClaim>.Success(
                new PlatformReserveLedgerClaim(Clone(row), Created: true), "Created."));
        }

        public Task<AZOAResult<PlatformReserveLedger>> UpdateIfVersionAsync(
            PlatformReserveLedger row, long expectedVersion, CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(row.Id, out var current) || current.Version != expectedVersion)
                return Task.FromResult(AZOAResult<PlatformReserveLedger>.Failure(
                    $"Reserve ledger row '{row.Id}' changed underneath this update " +
                    $"(expected version {expectedVersion})."));

            _rows[row.Id] = Clone(row);
            return Task.FromResult(AZOAResult<PlatformReserveLedger>.Success(Clone(row), "Saved."));
        }

        private static PlatformReserveLedger Clone(PlatformReserveLedger row) =>
            new()
            {
                Id = row.Id,
                AssetId = row.AssetId,
                Chain = row.Chain,
                Network = row.Network,
                ReserveAddress = row.ReserveAddress,
                TenantId = row.TenantId,
                UnitName = row.UnitName,
                Decimals = row.Decimals,
                TotalSupply = row.TotalSupply,
                Partitions = row.Partitions,
                RecentDrawKeys = row.RecentDrawKeys?.ToList(),
                Version = row.Version,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
            };
    }
}
