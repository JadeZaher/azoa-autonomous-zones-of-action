using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client.Query;
using User = AZOA.WebAPI.Persistence.SurrealDb.Models.User;
using Row = AZOA.WebAPI.Persistence.SurrealDb.Models.Wallet;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealConsentLifecycleTests
{
    [Theory]
    [InlineData("claimed")]
    [InlineData("revoked")]
    [InlineData("offboarded")]
    [InlineData("scope")]
    public async Task Tenant_bootstrap_checks_current_authority_even_when_wallet_already_exists(string change)
    {
        var authority = new SigningConsentWitness(1, Authority() with { ActorUserId = _tenant }, [AzoaScopes.TenantProvision], []);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_actor.ToString("N"))
            .Set(row => row.OwnerTenantId, "user:" + _tenant.ToString("N")).Set(row => row.ExternalUserId, "child").Build())).EnsureAllOk();
        var admitted = (await _store.AdmitProvisionedAsync(_actor, "child", authority))!;
        var walletAuthority = authority with { RequiredScopes = [AzoaScopes.TenantProvision, AzoaScopes.WalletManage, AzoaScopes.KycRead] };
        var candidate = new Wallet { Id = WalletBootstrapIdentity.For(_actor, "Algorand"), UserId = _actor,
            ChainType = "Algorand", Address = "bootstrap-fixture", WalletType = WalletType.Platform,
            EncryptedPrivateKey = "fixture-ciphertext", IsDefault = true };
        var wallets = new SurrealWalletStore(_executor);
        (await wallets.BootstrapTenantAsync(candidate, "child", walletAuthority)).IsError.Should().BeFalse();
        if (change == "claimed")
            (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_actor.ToString("N"))
                .Unset(row => row.OwnerTenantId).Set(row => row.AuthNotBefore, DateTimeOffset.UtcNow).Build())).EnsureAllOk();
        else if (change == "revoked")
            (await _store.RevokeUserExplicitAsync(admitted.GrantId, Authority())).Should().NotBeNull();
        else if (change == "offboarded")
            (await _store.OffboardAsync(_tenant, admitted.ParticipationRef!, authority)).Should().NotBeNull();
        else walletAuthority = authority;
        var refused = await wallets.BootstrapTenantAsync(candidate, "child", walletAuthority);
        refused.IsError.Should().BeTrue();
        (await _executor.QueryAsync<Row>(SurrealQuery<Row>.From())).Should().ContainSingle().Which.EncryptedPrivateKey.Should().Be("fixture-ciphertext");
    }

    [Fact]
    public async Task Tenant_bootstrap_uses_original_grant_with_additional_participation_and_preserves_default()
    {
        var authority = new SigningConsentWitness(1, Authority() with { ActorUserId = _tenant }, [AzoaScopes.TenantProvision], []);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_actor.ToString("N"))
            .Set(row => row.OwnerTenantId, "user:" + _tenant.ToString("N")).Set(row => row.ExternalUserId, "child").Build())).EnsureAllOk();
        var originalGrant = (await _store.AdmitProvisionedAsync(_actor, "child", authority))!;
        originalGrant.Should().NotBeNull();
        (await _store.AdmitParticipationAsync(new(Guid.NewGuid(), _tenant, [AzoaScopes.WalletManage, AzoaScopes.KycRead],
            GrantOrigin.Participation, originalGrant.ParticipationRef, null), Authority())).Should().NotBeNull();
        var walletAuthority = authority with { RequiredScopes = [AzoaScopes.TenantProvision, AzoaScopes.WalletManage, AzoaScopes.KycRead] };
        var wallets = new SurrealWalletStore(_executor);
        var prior = new Wallet { Id = Guid.NewGuid(), UserId = _actor, ChainType = "Algorand", Address = "owner-default",
            WalletType = WalletType.External, IsDefault = true };
        (await wallets.CreateIfAbsentAsync(prior)).IsError.Should().BeFalse();
        var candidate = new Wallet { Id = WalletBootstrapIdentity.For(_actor, "Algorand"), UserId = _actor,
            ChainType = "Algorand", Address = "new-custody", WalletType = WalletType.Platform,
            EncryptedPrivateKey = "fixture-ciphertext", IsDefault = true };
        var created = await wallets.BootstrapTenantAsync(candidate, "child", walletAuthority);
        created.IsError.Should().BeFalse(); created.Result!.IsDefault.Should().BeFalse();
        (await wallets.GetByIdAsync(prior.Id)).Result!.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Tenant_bootstrap_returns_the_committed_wallet_on_create_and_on_exact_replay()
    {
        var authority = new SigningConsentWitness(1, Authority() with { ActorUserId = _tenant }, [AzoaScopes.TenantProvision], []);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_actor.ToString("N"))
            .Set(row => row.OwnerTenantId, "user:" + _tenant.ToString("N")).Set(row => row.ExternalUserId, "child").Build())).EnsureAllOk();
        (await _store.AdmitProvisionedAsync(_actor, "child", authority)).Should().NotBeNull();
        var walletAuthority = authority with { RequiredScopes = [AzoaScopes.TenantProvision, AzoaScopes.WalletManage, AzoaScopes.KycRead] };
        var candidate = new Wallet { Id = WalletBootstrapIdentity.For(_actor, "Algorand"), UserId = _actor,
            ChainType = "Algorand", Address = "bootstrap-replay", WalletType = WalletType.Platform,
            EncryptedPrivateKey = "fixture-ciphertext", IsDefault = true };
        var wallets = new SurrealWalletStore(_executor);

        var created = await wallets.BootstrapTenantAsync(candidate, "child", walletAuthority);
        var replayed = await wallets.BootstrapTenantAsync(candidate, "child", walletAuthority);

        foreach (var outcome in new[] { created, replayed })
        {
            outcome.IsError.Should().BeFalse(outcome.Message);
            outcome.Result!.Id.Should().Be(candidate.Id);
            outcome.Result.Address.Should().Be("bootstrap-replay");
            outcome.Result.UserId.Should().Be(_actor);
        }
        (await _executor.QueryAsync<Row>(SurrealQuery<Row>.From())).Should().ContainSingle();
    }
}
