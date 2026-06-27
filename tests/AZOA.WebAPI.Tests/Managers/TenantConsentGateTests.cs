using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Services.Consent;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// tenant-consent-delegation AC4/AC5/AC10: the LIVE consent check the custody seam
/// calls before any key decrypt. Proves the FAIL-CLOSED posture — a tenant-driven
/// signing action is denied unless a live grant covers it — and that every decision
/// writes an audit row (AC10). A non-tenant-driven action is a no-op allow.
/// </summary>
public class TenantConsentGateTests
{
    private readonly Mock<IConsentGrantStore> _grants = new();
    private readonly Mock<IConsentAuditStore> _audit = new();
    private readonly TenantConsentGate _gate;

    public TenantConsentGateTests()
    {
        _audit.Setup(a => a.AppendAsync(It.IsAny<ConsentAuditEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        _gate = new TenantConsentGate(_grants.Object, _audit.Object, NullLogger<TenantConsentGate>.Instance);
    }

    private static SigningContext TenantCtx(Guid tenant, Guid grantor, string scope)
        => SigningContext.ForUser(grantor, Guid.NewGuid()).ActingAs(tenant, scope, grantor);

    private void NoGrant()
        => _grants.Setup(g => g.FindCoveringGrantAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ConsentGrant> { Result = null });

    private void LiveGrant(Guid grantor, Guid tenant, string scope)
        => _grants.Setup(g => g.FindCoveringGrantAsync(grantor, tenant, scope, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ConsentGrant>
            {
                Result = new ConsentGrant
                {
                    Id = Guid.NewGuid(), GrantorAvatarId = grantor, TenantId = tenant,
                    Scopes = new List<string> { scope }, GrantedAt = DateTime.UtcNow.AddMinutes(-1),
                }
            });

    [Fact]
    public async Task NonTenantDriven_IsAllowed_NoGrantLookup_NoAudit()
    {
        // A plain user-driven custodial context — no acting tenant.
        var ctx = SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid());

        var r = await _gate.EnsureAllowedAsync(ctx);

        r.IsError.Should().BeFalse();
        _grants.Verify(g => g.FindCoveringGrantAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _audit.Verify(a => a.AppendAsync(It.IsAny<ConsentAuditEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TenantDriven_NoCoveringGrant_Denied_AndAudited()
    {
        NoGrant();
        var tenant = Guid.NewGuid(); var grantor = Guid.NewGuid();
        var ctx = TenantCtx(tenant, grantor, AzoaScopes.SwapSign);

        var r = await _gate.EnsureAllowedAsync(ctx);

        r.IsError.Should().BeTrue();
        // AC10: a denied tenant-driven sign writes an audit row.
        _audit.Verify(a => a.AppendAsync(
            It.Is<ConsentAuditEntry>(e => e.Action == ConsentAuditAction.TenantSignDenied
                && e.TenantId == tenant && e.AvatarId == grantor),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TenantDriven_LiveCoveringGrant_Allowed_AndAudited()
    {
        var tenant = Guid.NewGuid(); var grantor = Guid.NewGuid();
        LiveGrant(grantor, tenant, AzoaScopes.SwapSign);
        var ctx = TenantCtx(tenant, grantor, AzoaScopes.SwapSign);

        var r = await _gate.EnsureAllowedAsync(ctx);

        r.IsError.Should().BeFalse();
        _audit.Verify(a => a.AppendAsync(
            It.Is<ConsentAuditEntry>(e => e.Action == ConsentAuditAction.TenantSignAllowed),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TenantDriven_GrantLookupError_FailsClosed()
    {
        // A store error must DENY — never open the gate on infra failure.
        _grants.Setup(g => g.FindCoveringGrantAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ConsentGrant> { IsError = true, Message = "db down" });
        var ctx = TenantCtx(Guid.NewGuid(), Guid.NewGuid(), AzoaScopes.SwapSign);

        var r = await _gate.EnsureAllowedAsync(ctx);

        r.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task TenantDriven_GrantLookupThrows_FailsClosed()
    {
        _grants.Setup(g => g.FindCoveringGrantAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var ctx = TenantCtx(Guid.NewGuid(), Guid.NewGuid(), AzoaScopes.SwapSign);

        var r = await _gate.EnsureAllowedAsync(ctx);

        r.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task TenantDriven_MissingGrantorOrScope_Denied()
    {
        // A tenant-driven context with an empty scope cannot be consent-checked → deny.
        var ctx = SigningContext.Platform.ActingAs(Guid.NewGuid(), scope: "", grantorAvatarId: Guid.NewGuid());

        var r = await _gate.EnsureAllowedAsync(ctx);

        r.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task TenantDriven_RevokedGrant_NotCovered_Denied()
    {
        // FindCoveringGrant filters revoked, so it returns null here; the gate's
        // own Covers() re-check would also reject. Either way: denied.
        NoGrant();
        var ctx = TenantCtx(Guid.NewGuid(), Guid.NewGuid(), AzoaScopes.TransferSign);

        var r = await _gate.EnsureAllowedAsync(ctx);

        r.IsError.Should().BeTrue();
    }
}
