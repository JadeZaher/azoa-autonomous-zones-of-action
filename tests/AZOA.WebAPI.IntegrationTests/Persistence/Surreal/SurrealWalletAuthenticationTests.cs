using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Auth;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using User = AZOA.WebAPI.Persistence.SurrealDb.Models.User;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealWalletAuthenticationTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealUserStore _users = null!;
    private WalletAuthIdentityMigrationService _migration = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "wallet_auth_identity_migration", "wallet_auth_challenge_budget", "wallet_auth_challenge", "wallet_auth_claim_token",
            "consent_grant", "consent_audit", "consent_webhook_event", "consent_lifecycle_transition", "webhook_registration");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = _namespace, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        _executor = new DefaultSurrealExecutor(_connection);
        _users = new(_executor);
        _migration = new(_executor);
    }

    public async Task DisposeAsync()
    {
        _connection.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Fact]
    public async Task Missing_gate_blocks_all_binding_writers_but_binding_free_profiles_remain_available()
    {
        var user = DomainUser();
        (await _users.CreateIfAbsentAsync(user)).IsError.Should().BeFalse();
        user.AuthWalletAddress = EthereumAddress;
        user.AuthWalletChainType = "ethereum";
        (await _users.UpsertAsync(user)).IsError.Should().BeTrue();
        (await _users.TryUpdateAuthenticationAsync(user, null, null, null, null)).IsError.Should().BeTrue();
        user = DomainUser();
        user.AuthWalletAddress = EthereumAddress;
        user.AuthWalletChainType = "ethereum";
        (await _users.CreateIfAbsentAsync(user)).IsError.Should().BeTrue();
        (await _users.GetByAuthWalletAsync(EthereumAddress, "ethereum")).IsError.Should().BeTrue();
        await _migration.SweepAsync(default);
        (await _migration.GetAsync(default)).Status.Should().Be("Ready");
        (await _users.CreateIfAbsentAsync(user)).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Claim_token_lookup_uses_a_non_reserved_binding_and_returns_only_the_exact_invite()
    {
        var tokens = new SurrealWalletAuthClaimTokenStore(_executor);
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var expected = await InviteAsync(userId, tenantId);
        var other = await InviteAsync(userId, tenantId);

        var found = await tokens.GetByTokenAsync(expected.Token);
        found.IsError.Should().BeFalse();
        found.Result.Should().NotBeNull();
        found.Result!.Id.Should().Be(expected.Id);
        found.Result.Token.Should().Be(expected.Token);
        found.Result.TargetUserId.Should().Be(userId);
        found.Result.TenantId.Should().Be(tenantId);
        found.Result.ConsumedAt.Should().BeNull();
        (await tokens.GetByTokenAsync(other.Token)).Result!.Id.Should().Be(other.Id);
        var missing = await tokens.GetByTokenAsync(Guid.NewGuid().ToString("N"));
        missing.IsError.Should().BeFalse();
        missing.Result.Should().BeNull();
    }

    [Fact]
    public async Task Never_linked_nullable_fields_CAS_and_claimed_unset_fields_round_trip()
    {
        await _migration.SweepAsync(default);
        var user = DomainUser();
        (await _users.CreateIfAbsentAsync(user)).IsError.Should().BeFalse();
        user.AuthWalletAddress = EthereumAddress.ToUpperInvariant().Replace("0X", "0x");
        user.AuthWalletChainType = "Ethereum";
        var linked = await _users.TryUpdateAuthenticationAsync(user, null, null, null, null);
        linked.IsError.Should().BeFalse();
        linked.Result!.AuthWalletAddress.Should().Be(EthereumAddress);
        (await _users.TryUpdateAuthenticationAsync(user, null, null, null, null)).IsError.Should().BeTrue();

        var child = DomainUser();
        child.OwnerTenantId = user.Id;
        (await _users.CreateIfAbsentAsync(child)).IsError.Should().BeFalse();
        child.OwnerTenantId = null;
        child.AuthNotBefore = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).UtcDateTime;
        child.PasswordHash = "new-hash";
        (await _users.TryClaimAuthenticationAsync(child, null, null, user.Id, null, await InviteAsync(child.Id, user.Id))).IsError.Should().BeFalse();
        child.AuthWalletAddress = new Algorand.Address(new byte[32]).ToString();
        child.AuthWalletChainType = "algorand";
        (await _users.TryUpdateAuthenticationAsync(child, null, null, null, child.AuthNotBefore)).IsError.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_identity_never_selects_owner_by_scan_order_and_operator_may_preserve_either(bool preserveFirst)
    {
        var first = Legacy("11111111111111111111111111111111", EthereumAddress.ToUpperInvariant().Replace("0X", "0x"));
        var second = Legacy("22222222222222222222222222222222", EthereumAddress);
        await CreateAsync(first);
        await CreateAsync(second);
        await _migration.SweepAsync(default);
        var state = await _migration.GetAsync(default);
        state.Status.Should().Be("Conflict");
        state.Conflicts.Select(item => Bare(item.UserId)).Should().BeEquivalentTo(first.Id, second.Id);
        (await _users.GetByAuthWalletAsync(EthereumAddress, "ethereum")).IsError.Should().BeTrue();
        var remove = preserveFirst ? second : first;
        var keep = preserveFirst ? first : second;
        var diagnosis = state.Conflicts.Single(item => Bare(item.UserId) == remove.Id);
        (await _migration.ClearConflictAsync(Guid.NewGuid(), state.Version, diagnosis.UserId, "stale-fingerprint", "verified ownership", default)).IsError.Should().BeTrue();
        (await _migration.ClearConflictAsync(Guid.NewGuid(), state.Version, diagnosis.UserId, diagnosis.ExpectedIdentity, "verified ownership", default)).IsError.Should().BeFalse();
        await _migration.SweepAsync(default);
        (await _migration.GetAsync(default)).Status.Should().Be("Ready");
        var owner = await _users.GetByAuthWalletAsync(EthereumAddress, "Ethereum");
        owner.Result!.Id.ToString("N").Should().Be(keep.Id);
        var cleared = await _executor.QuerySingleAsync<User>(SurrealQuery<User>.Key(remove.Id));
        cleared!.AuthWalletAddress.Should().BeNull();
    }

    [Fact]
    public async Task Expired_lease_resumes_partial_markers_and_concurrent_workers_cannot_publish_partial_readiness()
    {
        for (var index = 0; index < 105; index++)
            await CreateAsync(Legacy(Guid.NewGuid().ToString("N"), $"0x{index + 1:x40}"));
        await CreateAsync(new WalletAuthIdentityMigration
        {
            Status = "Migrating", LeaseToken = "crashed", LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), Version = 3,
        });
        var peer = new WalletAuthIdentityMigrationService(_executor);
        await Task.WhenAll(_migration.SweepAsync(default), peer.SweepAsync(default));
        for (var index = 0; index < 4 && (await _migration.GetAsync(default)).Status != "Ready"; index++) await peer.SweepAsync(default);
        (await _migration.GetAsync(default)).Status.Should().Be("Ready");
        var users = await _executor.QueryAsync<User>(SurrealQuery<User>.From());
        users.Should().HaveCount(105);
        users.Should().OnlyContain(user => user.AuthIdentityVersion == 1);
    }

    [Fact]
    public async Task Concurrent_challenges_admit_only_five_and_a_consumed_slot_can_be_reused()
    {
        await _migration.SweepAsync(default);
        var store = new SurrealWalletAuthChallengeStore(_executor);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(index => store.CreateAsync(Challenge(index))));
        attempts.Count(item => !item.IsError).Should().Be(5);
        var winner = attempts.First(item => !item.IsError).Result!;
        (await store.TryConsumeAsync(winner.Nonce, DateTime.UtcNow)).Result.Should().BeTrue();
        (await store.TryConsumeAsync(winner.Nonce, DateTime.UtcNow)).Result.Should().BeFalse();
        (await store.CreateAsync(Challenge(100))).IsError.Should().BeFalse();
        (await store.CountLiveByAddressAsync(EthereumAddress, "ethereum", DateTime.UtcNow)).Result.Should().Be(5);
    }

    [Fact]
    public async Task Stale_profile_preserves_credentials_and_role_and_logout_never_moves_watermark_backwards()
    {
        await _migration.SweepAsync(default);
        var user = DomainUser();
        (await _users.CreateIfAbsentAsync(user)).IsError.Should().BeFalse();
        (await _users.TryAssignDappRoleAsync(user.Id, user.DappRole, "dapp:developer")).IsError.Should().BeFalse();
        user.FirstName = "profile";
        user.PasswordHash = "stale-password";
        (await _users.UpsertAsync(user)).IsError.Should().BeFalse();
        var saved = (await _users.GetByIdAsync(user.Id)).Result!;
        saved.DappRole.Should().Be("dapp:developer");
        saved.PasswordHash.Should().Be("hash");
        (await _users.TrySetActiveAsync(user.Id, true, false)).IsError.Should().BeFalse();
        user.IsVerified = true;
        (await _users.UpsertAsync(user)).IsError.Should().BeFalse();
        var disabled = (await _users.GetByIdAsync(user.Id)).Result!;
        disabled.IsActive.Should().BeFalse();
        disabled.IsVerified.Should().BeFalse();
        var future = DateTime.UtcNow.AddMinutes(1);
        (await _users.AdvanceAuthenticationWatermarkAsync(user.Id, future)).IsError.Should().BeFalse();
        (await _users.AdvanceAuthenticationWatermarkAsync(user.Id, DateTime.UtcNow)).IsError.Should().BeFalse();
        (await _users.GetByIdAsync(user.Id)).Result!.AuthNotBefore.Should().BeCloseTo(future, TimeSpan.FromMilliseconds(1));
        (await _users.UpsertAsync(user)).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Claim_commits_all_tenant_revocations_and_notifications_with_one_ownership_winner()
    {
        await _migration.SweepAsync(default);
        var tenant = DomainUser(); var otherTenant = DomainUser(); var child = DomainUser();
        child.OwnerTenantId = tenant.Id;
        foreach (var user in new[] { tenant, otherTenant, child })
            (await _users.CreateIfAbsentAsync(user)).IsError.Should().BeFalse();
        foreach (var recipient in new[] { tenant.Id, otherTenant.Id })
            await CreateAsync(new ConsentGrant { Id = Guid.NewGuid().ToString("N"),
                GrantorUserId = "user:" + child.Id.ToString("N"), TenantId = "user:" + recipient.ToString("N"),
                Scopes = "action:execute", Origin = "UserExplicit", GrantedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        var competitor = (await _users.GetByIdAsync(child.Id)).Result!;
        child.OwnerTenantId = null; child.AuthNotBefore = DateTime.UtcNow; child.PasswordHash = "first-owner-secret";
        competitor.OwnerTenantId = null; competitor.AuthNotBefore = child.AuthNotBefore; competitor.PasswordHash = "other-owner-secret";
        var invite = await InviteAsync(child.Id, tenant.Id);
        var attempts = await Task.WhenAll(_users.TryClaimAuthenticationAsync(child, null, null, tenant.Id, null, invite),
            _users.TryClaimAuthenticationAsync(competitor, null, null, tenant.Id, null, invite));
        attempts.Count(item => !item.IsError).Should().Be(1);
        var grants = await _executor.QueryAsync<ConsentGrant>(SurrealQuery<ConsentGrant>.From());
        grants.Should().HaveCount(2).And.OnlyContain(row => row.RevokedAt != null && row.Version == 1);
        var events = await _executor.QueryAsync<ConsentWebhookEvent>(SurrealQuery<ConsentWebhookEvent>.From());
        events.Should().HaveCount(2).And.OnlyContain(row => row.EventType == ConsentWebhookEvent.WebhookEventType.Revoked);
        foreach (var notification in events)
        {
            var proof = (await _executor.QuerySingleAsync<ConsentLifecycleTransition>(SurrealQuery<ConsentLifecycleTransition>.Key(notification.TransitionId!)))!;
            AZOA.WebAPI.Helpers.ConsentNotificationEvidence.Matches(notification, proof).Should().BeTrue();
        }
        (await _users.GetByIdAsync(child.Id)).Result!.OwnerTenantId.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Committed_claim_returns_the_claimed_user_whatever_the_revocation_count(int grantCount)
    {
        await _migration.SweepAsync(default);
        var tenant = DomainUser(); var child = DomainUser(); child.OwnerTenantId = tenant.Id;
        (await _users.CreateIfAbsentAsync(tenant)).IsError.Should().BeFalse();
        (await _users.CreateIfAbsentAsync(child)).IsError.Should().BeFalse();
        for (var index = 0; index < grantCount; index++)
            await CreateAsync(new ConsentGrant { Id = Guid.NewGuid().ToString("N"),
                GrantorUserId = "user:" + child.Id.ToString("N"), TenantId = "user:" + tenant.Id.ToString("N"),
                Scopes = "action:execute", Origin = "UserExplicit", GrantedAt = DateTimeOffset.UtcNow.AddMinutes(-1 - index) });
        child.OwnerTenantId = null; child.AuthNotBefore = DateTime.UtcNow; child.PasswordHash = "owner-secret";

        var claimed = await _users.TryClaimAuthenticationAsync(child, null, null, tenant.Id, null, await InviteAsync(child.Id, tenant.Id));

        claimed.IsError.Should().BeFalse(claimed.Message);
        claimed.Result!.Id.Should().Be(child.Id);
        claimed.Result.OwnerTenantId.Should().BeNull();
        claimed.Result.PasswordHash.Should().Be("owner-secret");
        (await _users.GetByIdAsync(child.Id)).Result!.OwnerTenantId.Should().BeNull();
    }

    [Fact]
    public async Task Unadvanceable_grant_aborts_claim_without_partial_ownership_change()
    {
        await _migration.SweepAsync(default);
        var tenant = DomainUser(); var child = DomainUser(); child.OwnerTenantId = tenant.Id;
        (await _users.CreateIfAbsentAsync(tenant)).IsError.Should().BeFalse();
        (await _users.CreateIfAbsentAsync(child)).IsError.Should().BeFalse();
        await CreateAsync(new ConsentGrant { Id = Guid.NewGuid().ToString("N"),
            GrantorUserId = "user:" + child.Id.ToString("N"), TenantId = "user:" + tenant.Id.ToString("N"),
            Scopes = "action:execute", Origin = "UserExplicit", GrantedAt = DateTimeOffset.UtcNow.AddMinutes(-1), Version = long.MaxValue });
        child.OwnerTenantId = null; child.AuthNotBefore = DateTime.UtcNow; child.PasswordHash = "new-owner-secret";
        var invite = await InviteAsync(child.Id, tenant.Id);
        Func<Task> claim = () => _users.TryClaimAuthenticationAsync(child, null, null, tenant.Id, null, invite);
        await claim.Should().ThrowAsync<InvalidDataException>();
        var unchanged = (await _users.GetByIdAsync(child.Id)).Result!;
        unchanged.OwnerTenantId.Should().Be(tenant.Id); unchanged.PasswordHash.Should().Be("hash");
        (await new SurrealWalletAuthClaimTokenStore(_executor).GetByTokenAsync(invite.Token)).Result!.ConsumedAt.Should().BeNull();
        (await _executor.QueryAsync<ConsentWebhookEvent>(SurrealQuery<ConsentWebhookEvent>.From())).Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_conflict_rolls_back_invite_then_exact_retry_consumes_it_once()
    {
        await _migration.SweepAsync(default);
        var tenant = DomainUser(); var child = DomainUser(); child.OwnerTenantId = tenant.Id;
        (await _users.CreateIfAbsentAsync(tenant)).IsError.Should().BeFalse();
        (await _users.CreateIfAbsentAsync(child)).IsError.Should().BeFalse();
        var invite = await InviteAsync(child.Id, tenant.Id);
        child.OwnerTenantId = null; child.AuthNotBefore = DateTime.UtcNow; child.PasswordHash = "claimant-hash";
        var stale = await _users.TryClaimAuthenticationAsync(child, null, null, tenant.Id, DateTime.UtcNow.AddDays(-1), invite);
        stale.IsError.Should().BeTrue();
        var tokens = new SurrealWalletAuthClaimTokenStore(_executor);
        (await tokens.GetByTokenAsync(invite.Token)).Result!.ConsumedAt.Should().BeNull();
        (await _users.GetByIdAsync(child.Id)).Result!.OwnerTenantId.Should().Be(tenant.Id);
        (await _users.TryClaimAuthenticationAsync(child, null, null, tenant.Id, null, invite)).IsError.Should().BeFalse();
        (await tokens.GetByTokenAsync(invite.Token)).Result!.ConsumedAt.Should().NotBeNull();
        child.PasswordHash = "replay-secret";
        (await _users.TryClaimAuthenticationAsync(child, null, null, tenant.Id, null, invite)).IsError.Should().BeTrue();
        (await _users.GetByIdAsync(child.Id)).Result!.PasswordHash.Should().Be("claimant-hash");
    }

    private async Task<AZOA.WebAPI.Models.WalletAuthClaimToken> InviteAsync(Guid userId, Guid tenantId)
    {
        var invite = new AZOA.WebAPI.Models.WalletAuthClaimToken { Token = Guid.NewGuid().ToString("N"),
            TargetUserId = userId, TenantId = tenantId, ExpiresAt = DateTime.UtcNow.AddMinutes(5) };
        return (await new SurrealWalletAuthClaimTokenStore(_executor).CreateAsync(invite)).Result!;
    }

    private async Task CreateAsync<T>(T row) where T : ISurrealRecord, new()
    {
        var result = await _executor.ExecuteAsync(SurrealWriter.Create(row));
        result.EnsureAllOk();
    }

    private const string EthereumAddress = "0xabcdefabcdefabcdefabcdefabcdefabcdefabcd";
    private static string Bare(string id) => id.Split(':').Last();
    private static AZOA.WebAPI.Models.User DomainUser() => new()
    {
        Id = Guid.NewGuid(), Username = Guid.NewGuid().ToString("N"), Email = $"{Guid.NewGuid():N}@example.test", PasswordHash = "hash",
    };
    private static User Legacy(string id, string address) => new()
    {
        Id = id, Username = id, Email = $"{id}@example.test", PasswordHash = "hash", IsActive = true,
        AuthWalletAddress = address, AuthWalletChainType = "ethereum", CreatedDate = DateTimeOffset.UtcNow,
    };
    private static AZOA.WebAPI.Models.WalletAuthChallenge Challenge(int index) => new()
    {
        Id = Guid.NewGuid(), Nonce = $"challenge-{index}", Address = EthereumAddress, ChainType = "ethereum",
        DomainMessage = "fixture", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(4),
    };
}
