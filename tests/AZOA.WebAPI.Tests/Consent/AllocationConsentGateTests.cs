// SPDX-License-Identifier: UNLICENSED

using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Consent;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Consent;

/// <summary>
/// W3.6 — a tenant-key allocation is refused at the EDGE unless the target person has a
/// live consent grant covering the scope the allocation will sign under.
///
/// <para>These tests drive the controller through the REAL
/// <see cref="TenantConsentGate"/> over a mocked grant store, not a permissive mock, so
/// what is proved is the actual fail-closed decision rather than a stand-in for it. The
/// manager is a strict mock: "refused" means it was never called at all, which is what
/// makes the refusal free of side effects (no wallet generated, no holon written, no
/// idempotency claim burned).</para>
/// </summary>
public class AllocationConsentGateTests
{
    private readonly Mock<IAllocationManager> _manager = new();
    private readonly Mock<IConsentGrantStore> _grants = new();
    private readonly Mock<IConsentAuditStore> _audit = new();

    public AllocationConsentGateTests()
    {
        _audit.Setup(a => a.AppendAsync(It.IsAny<ConsentAuditEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        _manager.Setup(m => m.AllocateAsync(
                It.IsAny<Guid>(), It.IsAny<AllocationRequest>(), It.IsAny<Guid>(),
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .ReturnsAsync(AZOAResult<AllocationResult>.Success(new AllocationResult()));
    }

    // ── The headline: no grant ⇒ no allocation ────────────────────────────────

    [Fact]
    public async Task Allocate_TenantKeyAgainstAUserWithNoGrant_IsRefusedBeforeTheManagerRuns()
    {
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        NoCoveringGrant();
        var controller = CreateController(tenantId, RealGate());

        var result = await controller.Allocate(targetUserId, MintRequest());

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ManagerWasNeverCalled();
    }

    [Fact]
    public async Task Allocate_WithTheImpliedGrantLive_ReachesTheManager()
    {
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        GivenCoveringGrant(targetUserId, tenantId, AzoaScopes.NftMint, revokedAt: null);
        var controller = CreateController(tenantId, RealGate());

        var result = await controller.Allocate(targetUserId, MintRequest());

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Allocate_AfterThePersonRevokesTheGrant_IsRefusedAgain()
    {
        // Even if a stale row came back from the store, the gate re-evaluates liveness at the
        // seam — a revoked grant is inert the instant RevokedAt is written.
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        GivenCoveringGrant(targetUserId, tenantId, AzoaScopes.NftMint, revokedAt: DateTime.UtcNow.AddMinutes(-1));
        var controller = CreateController(tenantId, RealGate());

        var result = await controller.Allocate(targetUserId, MintRequest());

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ManagerWasNeverCalled();
    }

    [Fact]
    public async Task Allocate_WithAGrantForADifferentTenant_IsRefused()
    {
        // FindCoveringGrantAsync is scoped to (grantor, tenant); a grant to somebody else's
        // tenant simply is not found, and the gate fails closed on the miss.
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        GivenCoveringGrant(targetUserId, Guid.NewGuid(), AzoaScopes.NftMint, revokedAt: null);
        var controller = CreateController(tenantId, RealGate());

        var result = await controller.Allocate(targetUserId, MintRequest());

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ManagerWasNeverCalled();
    }

    [Fact]
    public async Task Allocate_WhenTheConsentStoreIsUnreadable_IsRefused()
    {
        var tenantId = Guid.NewGuid();
        _grants.Setup(g => g.FindCoveringGrantAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ConsentGrant> { IsError = true, Message = "store down" });
        var controller = CreateController(tenantId, RealGate());

        var result = await controller.Allocate(Guid.NewGuid(), MintRequest());

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ManagerWasNeverCalled();
    }

    [Fact]
    public async Task Allocate_WithNoConsentGateWired_IsRefusedNotWavedThrough()
    {
        var controller = CreateController(Guid.NewGuid(), consentGate: null);

        var result = await controller.Allocate(Guid.NewGuid(), MintRequest());

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ManagerWasNeverCalled();
    }

    // ── Acting-tenant derivation ──────────────────────────────────────────────

    [Fact]
    public async Task Allocate_DerivesTheActingTenantFromTheApiKeyOwner_AndTheGrantorFromTheRoute()
    {
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        SigningContext? probed = null;
        var gate = new Mock<ITenantConsentGate>();
        gate.Setup(g => g.EnsureAllowedAsync(It.IsAny<SigningContext>(), It.IsAny<CancellationToken>()))
            .Callback<SigningContext, CancellationToken>((ctx, _) => probed = ctx)
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        var controller = CreateController(tenantId, gate.Object);

        await controller.Allocate(targetUserId, MintRequest());

        probed.Should().NotBeNull();
        probed!.Value.IsTenantDriven.Should().BeTrue();
        probed.Value.ActingTenantId.Should().Be(tenantId);      // the API key's owner
        probed.Value.GrantorUserId.Should().Be(targetUserId);   // the route target, never a body field
        probed.Value.Scope.Should().Be(AzoaScopes.NftMint);

        // The same tenant id is what the manager receives, so the edge check and the deep
        // custody seam are asking about one identity, not two.
        _manager.Verify(m => m.AllocateAsync(
            targetUserId, It.IsAny<AllocationRequest>(), tenantId,
            It.IsAny<string?>(), It.IsAny<string>(), tenantId), Times.Once);
    }

    [Fact]
    public async Task Allocate_WhenThePrincipalNamesTwoDifferentTenants_IsRefused()
    {
        var tenantId = Guid.NewGuid();
        var controller = CreateController(
            tenantId, RealGate(), keyScopes: null,
            new Claim("act_as_tenant", Guid.NewGuid().ToString()));

        var result = await controller.Allocate(Guid.NewGuid(), MintRequest());

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ManagerWasNeverCalled();
    }

    // ── The default kind: issuance (OI-23) ────────────────────────────────────

    [Fact]
    public async Task Allocate_IssueUnits_WithTheIssuanceScopeGranted_ReachesTheManager()
    {
        // The headline. IssueUnits is the DEFAULT AllocationKind and the only one a tenant
        // may use (Mint is retired), so this is the single gate between "signed up" and "can
        // be paid" for a person who holds no AZOA credential and can never click "allow".
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        GivenCoveringGrant(targetUserId, tenantId, AzoaScopes.UnitsIssue, revokedAt: null);
        var controller = CreateController(tenantId, RealGate());

        var result = await controller.Allocate(targetUserId, IssueUnitsRequest());

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Allocate_TheDefaultRequestShape_IsAdmittedByTheImpliedGrantScope()
    {
        // A tenant that posts an allocation and names no kind gets IssueUnits. That request
        // must be admitted by the scope the implied participation grant actually carries —
        // this is the exact shape that used to 403 for every custodially onboarded person.
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        GivenCoveringGrant(targetUserId, tenantId, AzoaScopes.UnitsIssue, revokedAt: null);
        var controller = CreateController(tenantId, RealGate());

        var result = await controller.Allocate(targetUserId, new AllocationRequest());

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Allocate_IssueUnits_IsNotSatisfiedByTheOutboundTransferScope()
    {
        // transfer:sign keeps meaning money LEAVING an account. It is not a superset of
        // being credited, and a person who deliberately granted it has not thereby been
        // asked about issuance — the two scopes answer different questions.
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        GivenCoveringGrant(targetUserId, tenantId, AzoaScopes.TransferSign, revokedAt: null);
        var controller = CreateController(tenantId, RealGate());

        var result = await controller.Allocate(targetUserId, IssueUnitsRequest());

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ManagerWasNeverCalled();
    }

    [Fact]
    public async Task Allocate_IssueUnits_IsNotSatisfiedByTheMintScope()
    {
        // nft:mint also signs a Burn (AzoaScopes.OperationScopeMap), so an issuance must not
        // ride in on it — otherwise the credit path and a destructive path share one key.
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        GivenCoveringGrant(targetUserId, tenantId, AzoaScopes.NftMint, revokedAt: null);
        var controller = CreateController(tenantId, RealGate());

        var result = await controller.Allocate(targetUserId, IssueUnitsRequest());

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ManagerWasNeverCalled();
    }

    [Fact]
    public async Task Allocate_IssueUnits_ProbesTheIssuanceScopeAgainstTheRouteTarget()
    {
        // The probe the edge sends is the one the store is asked about: same grantor, same
        // tenant, same scope. If any of the three drifted, the gate would be checking a
        // question nobody answered.
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        GivenCoveringGrant(targetUserId, tenantId, AzoaScopes.UnitsIssue, revokedAt: null);
        var controller = CreateController(tenantId, RealGate());

        await controller.Allocate(targetUserId, IssueUnitsRequest());

        _grants.Verify(g => g.FindCoveringGrantAsync(
            targetUserId, tenantId, AzoaScopes.UnitsIssue,
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Allocate_IssueUnits_OnAKeyCarryingOnlyTheIssuanceScope_IsAdmitted()
    {
        // units:issue is a real key capability, not decorative vocabulary: a key that carries
        // only it may issue. It does NOT open the other kinds — see the next case.
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        GivenCoveringGrant(targetUserId, tenantId, AzoaScopes.UnitsIssue, revokedAt: null);
        var controller = CreateController(
            tenantId, RealGate(), keyScopes: new[] { AzoaScopes.UnitsIssue });

        var result = await controller.Allocate(targetUserId, IssueUnitsRequest());

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Allocate_Transfer_OnAKeyCarryingOnlyTheIssuanceScope_IsRefusedAtTheDoor()
    {
        // The issuance key scope admits an issuance and nothing else; a Transfer still needs
        // the scope it always needed, and is refused before any consent probe runs.
        var tenantId = Guid.NewGuid();
        var controller = CreateController(
            tenantId, RealGate(), keyScopes: new[] { AzoaScopes.UnitsIssue });

        var result = await controller.Allocate(
            Guid.NewGuid(),
            new AllocationRequest { Kind = AllocationKind.Transfer, ChainType = "Algorand" });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ManagerWasNeverCalled();
    }

    // ── Scope parity with the dispatch seam ───────────────────────────────────

    [Fact]
    public async Task Allocate_TransferKind_ChecksTheTransferScopeNotTheMintScope()
    {
        // A Transfer moves value OUT of an account, so it must not be satisfiable by the
        // inbound nft:mint scope the implied provisioning grant carries.
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        GivenCoveringGrant(targetUserId, tenantId, AzoaScopes.NftMint, revokedAt: null);
        var controller = CreateController(tenantId, RealGate());

        var result = await controller.Allocate(
            targetUserId, new AllocationRequest { Kind = AllocationKind.Transfer, ChainType = "Algorand" });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ManagerWasNeverCalled();
    }

    [Fact]
    public void SigningShape_MatchesTheScopeTheDispatchSeamWillAccept()
    {
        // The edge check must ask about the same capability the custody seam will, or a
        // person could consent to one thing and have another signed. AzoaScopes owns that
        // map (S6) and is the arbiter here.
        foreach (var kind in Enum.GetValues<AllocationKind>())
        {
            var (operationType, signingScope) = AllocationController.SigningShapeFor(kind);
            signingScope.Should().NotBeNull($"{kind} must declare a consent-checkable scope");
            AzoaScopes.IsScopeValidForOperation(operationType, signingScope).Should().BeTrue();
        }
    }

    [Fact]
    public void TheIssuanceScope_CannotSignAnythingButAnIssuance()
    {
        // THE ATTACK the S6 map exists to refuse, in the issuance direction: units:issue is
        // handed to every custodially onboarded person by the participation floor, so if it
        // could label an ordinary Transfer, that floor would silently authorise spending.
        AzoaScopes.IsScopeValidForOperation("Transfer", AzoaScopes.UnitsIssue).Should().BeFalse();
        AzoaScopes.IsScopeValidForOperation("Mint", AzoaScopes.UnitsIssue).Should().BeFalse();
        AzoaScopes.IsScopeValidForOperation("Swap", AzoaScopes.UnitsIssue).Should().BeFalse();
        AzoaScopes.IsScopeValidForOperation("Burn", AzoaScopes.UnitsIssue).Should().BeFalse();

        // And the reverse: an issuance cannot launder itself under an outbound scope.
        AzoaScopes.IsScopeValidForOperation(
            AzoaScopes.IssueUnitsOperationType, AzoaScopes.TransferSign).Should().BeFalse();
        AzoaScopes.IsScopeValidForOperation(
            AzoaScopes.IssueUnitsOperationType, AzoaScopes.NftMint).Should().BeFalse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AllocationRequest MintRequest()
        => new() { Kind = AllocationKind.Mint, ChainType = "Algorand", Amount = "100", Name = "Units" };

    private static AllocationRequest IssueUnitsRequest()
        => new()
        {
            Kind = AllocationKind.IssueUnits,
            ChainType = "Algorand",
            Amount = "100",
            AssetId = "1001",
            Partition = "contributor",
        };

    private TenantConsentGate RealGate()
        => new(_grants.Object, _audit.Object, new AZOA.WebAPI.Tests.TestSupport.ApprovedKybGate(), NullLogger<TenantConsentGate>.Instance);

    private void NoCoveringGrant()
        => _grants.Setup(g => g.FindCoveringGrantAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ConsentGrant> { Result = null });

    private void GivenCoveringGrant(Guid grantor, Guid tenant, string scope, DateTime? revokedAt)
    {
        NoCoveringGrant();
        _grants.Setup(g => g.FindCoveringGrantAsync(
                grantor, tenant, scope, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ConsentGrant>
            {
                Result = new ConsentGrant
                {
                    GrantorUserId = grantor,
                    TenantId = tenant,
                    Origin = GrantOrigin.Participation,
                    Scopes = new List<string> { scope },
                    RevokedAt = revokedAt,
                },
            });
    }

    private void ManagerWasNeverCalled()
        => _manager.Verify(m => m.AllocateAsync(
            It.IsAny<Guid>(), It.IsAny<AllocationRequest>(), It.IsAny<Guid>(),
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<Guid?>()), Times.Never);

    /// <summary>
    /// A tenant API-key principal: the key's owner user id IS the tenant id.
    /// <paramref name="keyScopes"/> defaults to <c>nft:mint</c>, the scope every tenant key
    /// already carries, so a test names it only when the door itself is what is under test.
    /// </summary>
    private AllocationController CreateController(
        Guid tenantId,
        ITenantConsentGate? consentGate,
        IEnumerable<string>? keyScopes = null,
        params Claim[] extraClaims)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, tenantId.ToString()),
            new("UserId", tenantId.ToString()),
            new("AuthMethod", "ApiKey"),
            new("ApiKeyId", Guid.NewGuid().ToString()),
        };
        claims.AddRange(
            (keyScopes ?? new[] { AzoaScopes.NftMint }).Select(scope => new Claim("scope", scope)));
        claims.AddRange(extraClaims);

        return new AllocationController(_manager.Object, consentGate)
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
