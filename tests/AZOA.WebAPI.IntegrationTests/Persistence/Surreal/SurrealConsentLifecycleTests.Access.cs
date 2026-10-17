using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Consent;
using FluentAssertions;
using SurrealForge.Client.Query;
using User = AZOA.WebAPI.Persistence.SurrealDb.Models.User;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealConsentLifecycleTests
{
    [Theory]
    [InlineData("claimed")]
    [InlineData("revoked")]
    [InlineData("offboarded")]
    [InlineData("tenant-revoked")]
    public async Task Status_release_rechecks_exact_current_authority_after_snapshot(string change)
    {
        var provisioning = new SigningConsentWitness(1, Authority() with { ActorUserId = _tenant }, [AzoaScopes.TenantProvision], []);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_actor.ToString("N"))
            .Set(value => value.OwnerTenantId, "user:" + _tenant.ToString("N")).Set(value => value.ExternalUserId, "child").Build())).EnsureAllOk();
        var original = (await _store.AdmitProvisionedAsync(_actor, "child", provisioning))!;
        var service = new TenantCustodialAccess(_executor);
        var authority = provisioning with { RequiredScopes = [AzoaScopes.TenantProvision, AzoaScopes.KycRead] };
        var captured = (await service.CaptureAsync(_actor, "child", authority, AzoaScopes.KycRead))!;
        captured.Should().NotBeNull();
        (await service.ValidateAsync(captured, AzoaScopes.KycRead)).Should().BeTrue();
        if (change == "claimed")
            (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_actor.ToString("N"))
                .Unset(value => value.OwnerTenantId).Set(value => value.AuthNotBefore, DateTimeOffset.UtcNow).Build())).EnsureAllOk();
        else if (change == "revoked") (await _store.RevokeUserExplicitAsync(original.GrantId, Authority())).Should().NotBeNull();
        else if (change == "offboarded") (await _store.OffboardAsync(_tenant, original.ParticipationRef!, provisioning)).Should().NotBeNull();
        else (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_tenant.ToString("N"))
            .Set(value => value.AuthNotBefore, DateTimeOffset.UtcNow).Build())).EnsureAllOk();
        (await service.ValidateAsync(captured, AzoaScopes.KycRead)).Should().BeFalse();
    }
}
