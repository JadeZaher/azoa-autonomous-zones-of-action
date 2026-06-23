using FluentAssertions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// tenant-consent-delegation: the consent authority. Proves the H4 value-scope
/// exclusion for Participation grants, the AC9 IDOR isolation (cross-user probe →
/// NotFound), the AC10 audit on grant/revoke, the AC7 webhook emit, and the AC5
/// revoke semantics (RevokedAt set, makes the grant inert).
/// </summary>
public class ConsentManagerTests
{
    private readonly Mock<IConsentGrantStore> _grants = new();
    private readonly Mock<IConsentAuditStore> _audit = new();
    private readonly Mock<IConsentWebhookEmitter> _webhook = new();
    private readonly ConsentManager _mgr;

    public ConsentManagerTests()
    {
        _grants.Setup(g => g.UpsertAsync(It.IsAny<ConsentGrant>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsentGrant g, CancellationToken _) => new AZOAResult<ConsentGrant> { Result = g });
        _audit.Setup(a => a.AppendAsync(It.IsAny<ConsentAuditEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        _webhook.Setup(w => w.EmitAsync(It.IsAny<ConsentWebhookEventType>(), It.IsAny<ConsentGrant>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mgr = new ConsentManager(_grants.Object, _audit.Object, _webhook.Object);
    }

    [Fact]
    public async Task Grant_UserExplicit_WithValueScope_Succeeds()
    {
        var user = Guid.NewGuid(); var tenant = Guid.NewGuid();
        var r = await _mgr.GrantAsync(user, tenant, new[] { AzoaScopes.SwapSign }, GrantOrigin.UserExplicit, null, null);

        r.IsError.Should().BeFalse();
        r.Result!.GrantorAvatarId.Should().Be(user);
        // AC10 audit + AC7 webhook on grant.
        _audit.Verify(a => a.AppendAsync(It.Is<ConsentAuditEntry>(e => e.Action == ConsentAuditAction.Granted), It.IsAny<CancellationToken>()), Times.Once);
        _webhook.Verify(w => w.EmitAsync(ConsentWebhookEventType.Granted, It.IsAny<ConsentGrant>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Grant_Participation_WithValueScope_Rejected_H4()
    {
        var user = Guid.NewGuid(); var tenant = Guid.NewGuid();
        // A Participation grant MUST NOT carry a value-signing scope.
        var r = await _mgr.GrantAsync(user, tenant, new[] { AzoaScopes.QuestExecute, AzoaScopes.SwapSign },
            GrantOrigin.Participation, "participation-1", null);

        r.IsError.Should().BeTrue();
        r.Message.Should().Contain(AzoaScopes.SwapSign);
        _grants.Verify(g => g.UpsertAsync(It.IsAny<ConsentGrant>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Grant_Participation_NonValueScopes_Succeeds()
    {
        var user = Guid.NewGuid(); var tenant = Guid.NewGuid();
        var r = await _mgr.GrantParticipationAsync(user, tenant, "participation-1", new[] { AzoaScopes.QuestExecute });

        r.IsError.Should().BeFalse();
        r.Result!.Origin.Should().Be(GrantOrigin.Participation);
        r.Result.ParticipationRef.Should().Be("participation-1");
    }

    [Fact]
    public async Task Revoke_CrossUser_ReturnsNotFound_NotForbidden_AC9()
    {
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        _grants.Setup(g => g.GetByIdAsync(grantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ConsentGrant>
            {
                Result = new ConsentGrant { Id = grantId, GrantorAvatarId = owner, TenantId = Guid.NewGuid() }
            });

        var r = await _mgr.RevokeAsync(attacker, grantId, default);

        r.IsError.Should().BeTrue();
        r.Message.Should().StartWith(TenantAuthorizationError.NotFound);
        r.Message.Should().NotStartWith(TenantAuthorizationError.Forbidden);
    }

    [Fact]
    public async Task Revoke_Owner_SetsRevokedAt_AuditsAndEmits_AC5_AC7()
    {
        var owner = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        _grants.Setup(g => g.GetByIdAsync(grantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ConsentGrant>
            {
                Result = new ConsentGrant { Id = grantId, GrantorAvatarId = owner, TenantId = Guid.NewGuid(),
                    Scopes = new List<string> { AzoaScopes.SwapSign } }
            });

        var r = await _mgr.RevokeAsync(owner, grantId, default);

        r.IsError.Should().BeFalse();
        // RevokedAt is set on the persisted grant — inert the instant it's written (AC5).
        _grants.Verify(g => g.UpsertAsync(It.Is<ConsentGrant>(x => x.RevokedAt != null), It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.AppendAsync(It.Is<ConsentAuditEntry>(e => e.Action == ConsentAuditAction.Revoked), It.IsAny<CancellationToken>()), Times.Once);
        _webhook.Verify(w => w.EmitAsync(ConsentWebhookEventType.Revoked, It.IsAny<ConsentGrant>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RevokeByParticipation_ExactMatch_WithinTenantGrantsOnly_L3()
    {
        var tenant = Guid.NewGuid();
        var g1 = new ConsentGrant { Id = Guid.NewGuid(), GrantorAvatarId = Guid.NewGuid(), TenantId = tenant,
            Origin = GrantOrigin.Participation, ParticipationRef = "p-1", Scopes = new List<string> { AzoaScopes.QuestExecute } };
        _grants.Setup(g => g.ListByTenantAndParticipationRefAsync(tenant, "p-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<ConsentGrant>> { Result = new[] { g1 } });

        var r = await _mgr.RevokeByParticipationAsync(tenant, "p-1", default);

        r.IsError.Should().BeFalse();
        r.Result.Should().Be(1);
        _grants.Verify(g => g.UpsertAsync(It.Is<ConsentGrant>(x => x.RevokedAt != null), It.IsAny<CancellationToken>()), Times.Once);
    }
}
