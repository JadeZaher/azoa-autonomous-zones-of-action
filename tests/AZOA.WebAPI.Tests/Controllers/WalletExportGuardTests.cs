using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Controllers;

/// <summary>
/// Adversarial coverage for the custody-export bypass fix (security-review).
/// <para><b>The attack.</b> Raw-key export is the ONE path that emits cleartext signing
/// material, sidestepping the consent gate entirely. A tenant-driven child credential
/// (<c>act_as_tenant</c>) carries the USER's avatar id as its subject — so without a
/// guard a tenant could call <c>POST /wallet/{id}/export</c>, exfiltrate the user's
/// private key, and sign offline forever, defeating every consent grant, scope ceiling,
/// and revocation.</para>
/// <para><b>The fix.</b> <c>WalletController.Export</c> reads
/// <c>User.GetActingTenantId()</c> and <c>Forbid()</c>s a tenant-driven principal BEFORE
/// the manager is ever touched. Export is a USER-ONLY action.</para>
/// </summary>
public class WalletExportGuardTests
{
    private readonly Mock<IWalletManager> _walletManager = new();

    private static ClaimsPrincipal PrincipalWith(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "test"));

    private WalletController BuildController(ClaimsPrincipal principal)
    {
        // WalletController.GetAvatarIdFromClaims reads ClaimTypes.NameIdentifier first,
        // then "sub" — principals here use ClaimTypes.NameIdentifier to match exactly.
        var controller = new WalletController(_walletManager.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };
        return controller;
    }

    [Fact]
    public async Task Export_TenantDrivenChildCredential_IsForbidden()
    {
        var avatarId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        // A tenant-driven child credential: valid sub (the USER's avatar id) AND
        // act_as_tenant. The shared subject is exactly what makes the bypass possible.
        var principal = PrincipalWith(
            new Claim(ClaimTypes.NameIdentifier, avatarId.ToString()),
            new Claim("act_as_tenant", tenantId.ToString()));
        var controller = BuildController(principal);

        var result = await controller.Export(Guid.NewGuid(), null);

        // Authenticated but not permitted → 403, not 404/401.
        result.Result.Should().BeOfType<ForbidResult>();
        // The manager — the only thing that can decrypt the key — is NEVER reached.
        _walletManager.Verify(m => m.ExportWalletAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<AZOARequest>()), Times.Never);
    }

    [Fact]
    public async Task Export_PlainUser_IsAllowedThrough()
    {
        var avatarId = Guid.NewGuid();
        var walletId = Guid.NewGuid();
        // A plain user login: sub but NO act_as_tenant.
        var principal = PrincipalWith(new Claim(ClaimTypes.NameIdentifier, avatarId.ToString()));
        var controller = BuildController(principal);

        _walletManager.Setup(m => m.ExportWalletAsync(walletId, avatarId, It.IsAny<AZOARequest>()))
            .ReturnsAsync(new AZOAResult<WalletExportResult>
            {
                Result = new WalletExportResult
                {
                    WalletId = walletId,
                    ChainType = "algorand",
                    Address = "ADDR",
                    PrivateKey = "deadbeef",
                }
            });

        var result = await controller.Export(walletId, null);

        result.Result.Should().BeOfType<OkObjectResult>();
        // The user's own export DOES go through to the manager.
        _walletManager.Verify(m => m.ExportWalletAsync(walletId, avatarId, It.IsAny<AZOARequest>()), Times.Once);
    }

    [Fact]
    public async Task Export_Unauthenticated_NoSub_ReturnsUnauthorized()
    {
        // No sub claim at all → the avatar-id resolution fails before any guard.
        var principal = PrincipalWith(new Claim("irrelevant", "x"));
        var controller = BuildController(principal);

        var result = await controller.Export(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        _walletManager.Verify(m => m.ExportWalletAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<AZOARequest>()), Times.Never);
    }
}
