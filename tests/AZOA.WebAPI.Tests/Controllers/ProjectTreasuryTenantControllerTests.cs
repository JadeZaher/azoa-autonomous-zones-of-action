// SPDX-License-Identifier: UNLICENSED
// L1.2 / OI-24a — the tenant-scoped project treasury resolve route: a tenant gets its own
// project an account for funds to land in, without an operator credential, and can never
// reach anybody else's.

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

public sealed class ProjectTreasuryTenantControllerTests
{
    private const string Chain = "Algorand";
    private const string ProjectAlpha = "project-alpha";
    private const string DerivedAddress = "TREASURYADDRESSALPHA";

    private static readonly Guid TenantOne = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantTwo = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ── The tenant comes from the principal ───────────────────────────────────

    [Fact]
    public async Task Resolve_takes_the_tenant_from_the_principal_and_never_from_the_body()
    {
        var treasury = TreasuryReturning(AccountFor(TenantOne), out var captured);
        var controller = CreateController(treasury.Object, TenantOne, AzoaScopes.TenantProvision);

        var result = await controller.Resolve(Request(), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        captured.Value.Should().NotBeNull();
        captured.Value!.TenantId.Should().Be(
            TenantOne, "the reference is built from the credential's own subject");
        captured.Value.NormalizedProjectReference.Should().Be(ProjectAlpha);
        captured.Value.NormalizedChainType.Should().Be(Chain);
    }

    [Fact]
    public async Task Resolve_scopes_two_tenants_asking_for_one_project_id_to_two_references()
    {
        var first = TreasuryReturning(AccountFor(TenantOne), out var firstCaptured);
        var second = TreasuryReturning(AccountFor(TenantTwo), out var secondCaptured);

        await CreateController(first.Object, TenantOne, AzoaScopes.TenantProvision)
            .Resolve(Request(), CancellationToken.None);
        await CreateController(second.Object, TenantTwo, AzoaScopes.TenantProvision)
            .Resolve(Request(), CancellationToken.None);

        firstCaptured.Value!.TenantId.Should().Be(TenantOne);
        secondCaptured.Value!.TenantId.Should().Be(
            TenantTwo, "a project id is only unique inside its own tenant");
    }

    [Fact]
    public async Task Resolve_refuses_a_principal_naming_two_different_acting_tenants()
    {
        var treasury = TreasuryReturning(AccountFor(TenantOne), out _);
        var controller = CreateController(
            treasury.Object,
            TenantOne,
            new Claim("scope", AzoaScopes.TenantProvision),
            new Claim("act_as_tenant", TenantTwo.ToString()));

        var result = await controller.Resolve(Request(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status403Forbidden);
        NeverResolved(treasury);
    }

    [Fact]
    public async Task Resolve_refuses_a_principal_without_the_tenant_provision_scope()
    {
        var treasury = TreasuryReturning(AccountFor(TenantOne), out _);
        var controller = CreateController(treasury.Object, TenantOne, AzoaScopes.DappDevelop);

        var result = await controller.Resolve(Request(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status403Forbidden);
        NeverResolved(treasury);
    }

    [Fact]
    public async Task Resolve_refuses_a_principal_with_no_resolvable_identity()
    {
        var treasury = TreasuryReturning(AccountFor(TenantOne), out _);
        var controller = CreateController(
            treasury.Object, subject: null, new Claim("scope", AzoaScopes.TenantProvision));

        var result = await controller.Resolve(Request(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status401Unauthorized);
        NeverResolved(treasury);
    }

    // ── What the body may say ─────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_refuses_a_blank_chain_before_touching_the_treasury()
    {
        var treasury = TreasuryReturning(AccountFor(TenantOne), out _);
        var controller = CreateController(treasury.Object, TenantOne, AzoaScopes.TenantProvision);

        var result = await controller.Resolve(Request(chainType: "  "), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        NeverResolved(treasury);
    }

    [Fact]
    public async Task Resolve_refuses_a_blank_project_reference_before_touching_the_treasury()
    {
        var treasury = TreasuryReturning(AccountFor(TenantOne), out _);
        var controller = CreateController(treasury.Object, TenantOne, AzoaScopes.TenantProvision);

        var result = await controller.Resolve(Request(projectReference: ""), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        NeverResolved(treasury);
    }

    [Fact]
    public void The_resolve_body_has_no_field_that_could_name_a_tenant()
    {
        // The rule "the tenant comes from the principal, never the body" is only a rule while
        // the body has nowhere to put one. A future property called TenantId would satisfy the
        // model binder silently, so the shape of the request type is itself the control.
        var names = typeof(TenantProjectTreasuryResolveRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        names.Should().BeEquivalentTo(
            nameof(TenantProjectTreasuryResolveRequest.ChainType),
            nameof(TenantProjectTreasuryResolveRequest.ProjectReference));
        names.Should().NotContain(name => name.Contains("Tenant", StringComparison.OrdinalIgnoreCase));
    }

    // ── What comes back ───────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_returns_the_reference_chain_and_status_the_node_client_records()
    {
        var treasury = TreasuryReturning(AccountFor(TenantOne), out _);
        var controller = CreateController(treasury.Object, TenantOne, AzoaScopes.TenantProvision);

        var result = await controller.Resolve(Request(), CancellationToken.None);

        var payload = BodyOf(result);
        payload.IsError.Should().BeFalse(payload.Message);
        payload.Result!.ChainType.Should().Be(Chain);
        payload.Result.ProjectReference.Should().Be(ProjectAlpha);
        payload.Result.Status.Should().Be(nameof(ProjectTreasuryStatus.Open));
        payload.Result.TreasuryAddress.Should().Be(
            DerivedAddress, "the node client has to know where it sent the funds");
    }

    [Fact]
    public async Task Resolve_refuses_to_return_a_treasury_recorded_against_another_tenant()
    {
        // The service refuses this first (ProjectTreasuryService.GuardRowOwnership, at the
        // read). This asserts the door refuses it too, so a regression in either one alone
        // still cannot put another tenant's account on the wire.
        var treasury = TreasuryReturning(AccountFor(TenantTwo), out _);
        var controller = CreateController(treasury.Object, TenantOne, AzoaScopes.TenantProvision);

        var result = await controller.Resolve(Request(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status403Forbidden);
        var body = BodyOf(result);
        body.Result.Should().BeNull("a refusal must carry no address at all");
        body.Code.Should().Be(AzoaErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Resolve_refuses_a_closed_treasury_rather_than_naming_it_as_where_funds_land()
    {
        var treasury = TreasuryReturning(
            AccountFor(TenantOne, status: ProjectTreasuryStatus.Closed), out _);
        var controller = CreateController(treasury.Object, TenantOne, AzoaScopes.TenantProvision);

        var result = await controller.Resolve(Request(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        BodyOf(result).Result.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_surfaces_a_service_refusal_and_invents_no_address()
    {
        var treasury = new Mock<IProjectTreasury>();
        treasury
            .Setup(candidate => candidate.ResolveAsync(
                It.IsAny<ProjectTreasuryReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ProjectTreasuryAccount>.Failure(
                "No project treasury account is available on 'Algorand'."));
        var controller = CreateController(treasury.Object, TenantOne, AzoaScopes.TenantProvision);

        var result = await controller.Resolve(Request(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        var body = BodyOf(result);
        body.Result.Should().BeNull();
        body.Message.Should().Contain("Algorand");
    }

    // ── The guard at the counter, not at the door ─────────────────────────────

    [Fact]
    public async Task GetLedgerAsync_refuses_a_row_recorded_against_another_tenant()
    {
        var service = ServiceOver(RowFor(TenantTwo));

        var row = await service.GetLedgerAsync(new ProjectTreasuryReference(Chain, TenantOne, ProjectAlpha));

        row.IsError.Should().BeTrue(
            "a read reaches its row through a tenant-scoped record id, so a row carrying "
            + "another tenant's id must never be returned");
        row.Result.Should().BeNull();
        row.Message.Should().NotContain(TenantTwo.ToString("N"), "a refusal says nothing about whose it is");
    }

    [Fact]
    public async Task GetLedgerAsync_returns_the_row_when_the_recorded_tenant_matches()
    {
        var service = ServiceOver(RowFor(TenantOne));

        var row = await service.GetLedgerAsync(new ProjectTreasuryReference(Chain, TenantOne, ProjectAlpha));

        row.IsError.Should().BeFalse(row.Message);
        row.Result!.ProjectReference.Should().Be(ProjectAlpha);
    }

    // ─── fixtures ───

    private static TenantProjectTreasuryResolveRequest Request(
        string chainType = Chain, string projectReference = ProjectAlpha) =>
        new() { ChainType = chainType, ProjectReference = projectReference };

    private static ProjectTreasuryAccount AccountFor(
        Guid tenantId, ProjectTreasuryStatus status = ProjectTreasuryStatus.Open) =>
        new(Chain, tenantId, ProjectAlpha, DerivedAddress, ProjectTreasuryKeyDerivation.Version, status);

    /// <summary>A treasury double that answers with <paramref name="account"/> and records the
    /// reference the controller built.</summary>
    private static Mock<IProjectTreasury> TreasuryReturning(
        ProjectTreasuryAccount account, out CapturedReference captured)
    {
        var capture = new CapturedReference();
        captured = capture;

        var treasury = new Mock<IProjectTreasury>();
        treasury
            .Setup(candidate => candidate.ResolveAsync(
                It.IsAny<ProjectTreasuryReference>(), It.IsAny<CancellationToken>()))
            .Callback<ProjectTreasuryReference, CancellationToken>((reference, _) => capture.Value = reference)
            .ReturnsAsync(AZOAResult<ProjectTreasuryAccount>.Success(account, "Project treasury account resolved."));
        return treasury;
    }

    private sealed class CapturedReference
    {
        public ProjectTreasuryReference? Value { get; set; }
    }

    private static void NeverResolved(Mock<IProjectTreasury> treasury) =>
        treasury.Verify(
            candidate => candidate.ResolveAsync(
                It.IsAny<ProjectTreasuryReference>(), It.IsAny<CancellationToken>()),
            Times.Never);

    private static ProjectTreasuryTenantController CreateController(
        IProjectTreasury treasuries, Guid subject, string scope) =>
        CreateController(treasuries, subject, new Claim("scope", scope));

    private static ProjectTreasuryTenantController CreateController(
        IProjectTreasury treasuries, Guid? subject, params Claim[] claims)
    {
        var all = new List<Claim> { new("AuthMethod", "ApiKey") };
        if (subject is { } id)
        {
            all.Add(new Claim(ClaimTypes.NameIdentifier, id.ToString()));
            all.Add(new Claim("UserId", id.ToString()));
        }

        all.AddRange(claims);

        return new ProjectTreasuryTenantController(treasuries)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(all, "TestApiKey")),
                },
            },
        };
    }

    private static int StatusOf(ActionResult<AZOAResult<TenantProjectTreasuryResponse>> result) =>
        result.Result switch
        {
            ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
            StatusCodeResult statusResult => statusResult.StatusCode,
            _ => StatusCodes.Status200OK,
        };

    private static AZOAResult<TenantProjectTreasuryResponse> BodyOf(
        ActionResult<AZOAResult<TenantProjectTreasuryResponse>> result) =>
        result.Result.Should().BeAssignableTo<ObjectResult>().Which
            .Value.Should().BeOfType<AZOAResult<TenantProjectTreasuryResponse>>().Subject;

    private static ProjectTreasuryLedger RowFor(Guid tenantId) => new()
    {
        Id = ProjectTreasuryLedger.RecordIdFor(
            Chain, ChainNetwork.Devnet.ToString(), TenantOne.ToString("N"), ProjectAlpha),
        Chain = Chain,
        Network = ChainNetwork.Devnet.ToString(),
        TenantId = $"user:{tenantId:N}",
        ProjectReference = ProjectAlpha,
        TreasuryAddress = DerivedAddress,
        DerivationVersion = ProjectTreasuryKeyDerivation.Version,
        Status = nameof(ProjectTreasuryStatus.Open),
        Version = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>The real service over a store that serves exactly one row; no key is ever
    /// derived because <c>GetLedgerAsync</c> is a pure read.</summary>
    private static ProjectTreasuryService ServiceOver(ProjectTreasuryLedger row)
    {
        var factory = new Mock<IBlockchainProviderFactory>();
        factory.SetupGet(candidate => candidate.DefaultNetwork).Returns(ChainNetwork.Devnet);

        return new ProjectTreasuryService(
            new Mock<IKeyCustodyService>(MockBehavior.Strict).Object,
            factory.Object,
            new SingleRowLedgerStore(row),
            new ReserveOptions());
    }

    private sealed class SingleRowLedgerStore : IProjectTreasuryLedgerStore
    {
        private readonly ProjectTreasuryLedger _row;

        public SingleRowLedgerStore(ProjectTreasuryLedger row) => _row = row;

        public Task<AZOAResult<ProjectTreasuryLedger?>> GetAsync(string recordId, CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<ProjectTreasuryLedger?>.Success(
                string.Equals(recordId, _row.Id, StringComparison.Ordinal) ? _row : null, "Success"));

        public Task<AZOAResult<ProjectTreasuryLedgerClaim>> CreateIfAbsentAsync(
            ProjectTreasuryLedger row, CancellationToken ct = default) =>
            throw new NotSupportedException("These tests only read.");

        public Task<AZOAResult<ProjectTreasuryLedger>> UpdateIfVersionAsync(
            ProjectTreasuryLedger row, long expectedVersion, CancellationToken ct = default) =>
            throw new NotSupportedException("These tests only read.");

        public Task<AZOAResult<IReadOnlyList<ProjectTreasuryLedger>>> ListByTenantAsync(
            string tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException("These tests only read.");
    }
}
