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
            tenantId, RealGate(), new Claim("act_as_tenant", Guid.NewGuid().ToString()));

        var result = await controller.Allocate(Guid.NewGuid(), MintRequest());

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
        foreach (var kind in new[] { AllocationKind.Mint, AllocationKind.Transfer })
        {
            var (operationType, signingScope) = AllocationController.SigningShapeFor(kind);
            signingScope.Should().NotBeNull();
            AzoaScopes.IsScopeValidForOperation(operationType, signingScope).Should().BeTrue();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AllocationRequest MintRequest()
        => new() { Kind = AllocationKind.Mint, ChainType = "Algorand", Amount = "100", Name = "Units" };

    private TenantConsentGate RealGate()
        => new(_grants.Object, _audit.Object, NullLogger<TenantConsentGate>.Instance);

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

    /// <summary>A tenant API-key principal: the key's owner user id IS the tenant id.</summary>
    private AllocationController CreateController(
        Guid tenantId, ITenantConsentGate? consentGate, params Claim[] extraClaims)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, tenantId.ToString()),
            new("UserId", tenantId.ToString()),
            new("AuthMethod", "ApiKey"),
            new("ApiKeyId", Guid.NewGuid().ToString()),
            new("scope", AzoaScopes.NftMint),
        };
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
