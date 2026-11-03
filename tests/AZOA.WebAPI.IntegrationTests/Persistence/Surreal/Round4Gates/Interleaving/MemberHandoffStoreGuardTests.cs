using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Member = AZOA.WebAPI.Persistence.SurrealDb.Models.User;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Interleaving;

public sealed class MemberHandoffStoreGuardTests : IAsyncLifetime
{
    private InterleavingDatabase _database = null!;
    private SurrealMemberHandoffStore _store = null!;
    private readonly Guid _tenant = Guid.NewGuid();
    private SigningConsentWitness _authority = null!;

    public async Task InitializeAsync()
    {
        _database = await InterleavingDatabase.CreateAsync("user", "member_handoff", "wallet_auth_claim_token");
        _store = new SurrealMemberHandoffStore(_database.Executor);
        await Write(SurrealWriter.Create(User(_tenant)));
        var now = DateTimeOffset.UtcNow;
        _authority = new(1, new SigningCredentialWitness(1, "Jwt", _tenant, null, null,
            now.AddMinutes(-1), now.AddHours(1), null, null, [], true, TokenUse: AzoaClaims.TokenUseLogin),
            [AzoaScopes.TenantProvision], []);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Disabled_or_stale_member_refuses_delivery_and_leaves_no_partial_retirement()
    {
        foreach (var change in new[] { "disabled", "owner", "external", "watermark", "advanced-watermark" })
        {
            var member = Guid.NewGuid();
            var external = "member-" + Guid.NewGuid().ToString("N");
            var original = User(member);
            original.OwnerTenantId = Link(_tenant);
            original.ExternalUserId = external;
            DateTimeOffset? expectedWatermark = change == "advanced-watermark" ? DateTimeOffset.UtcNow.AddMinutes(-2) : null;
            original.AuthNotBefore = expectedWatermark;
            await Write(SurrealWriter.Create(original));
            var old = Delivery(member, external);
            await Write(SurrealWriter.Create(old.Invite));
            var candidate = Delivery(member, external);
            switch (change)
            {
                case "disabled":
                    await Write(SurrealWriter.UpdateOnly<Member>(member.ToString("N")).Where(row => row.IsActive).Set(row => row.IsActive, false).Build());
                    break;
                case "owner":
                    var replacementOwner = Guid.NewGuid();
                    await Write(SurrealWriter.Create(User(replacementOwner)));
                    await Write(SurrealWriter.UpdateOnly<Member>(member.ToString("N")).Where(row => row.IsActive).Set(row => row.OwnerTenantId, Link(replacementOwner)).Build());
                    break;
                case "external":
                    await Write(SurrealWriter.UpdateOnly<Member>(member.ToString("N")).Where(row => row.IsActive).Set(row => row.ExternalUserId, external + "-changed").Build());
                    break;
                case "watermark":
                case "advanced-watermark":
                    await Write(SurrealWriter.UpdateOnly<Member>(member.ToString("N")).Where(row => row.IsActive).Set(row => row.AuthNotBefore, DateTimeOffset.UtcNow).Build());
                    break;
            }

            var current = await Read<Member>(member.ToString("N"));
            var action = () => _store.SaveDeliveryAsync(candidate.Row, null, candidate.Invite, member,
                Link(_tenant), expectedWatermark, _authority, CancellationToken.None);
            await action.Should().ThrowAsync<SurrealStatementException>().WithMessage("*HANDOFF_AUTHORITY_CHANGED*");
            (await Read<MemberHandoff>(candidate.Row.Id)).Should().BeNull();
            (await Read<WalletAuthClaimToken>(candidate.Invite.Id)).Should().BeNull();
            (await Read<WalletAuthClaimToken>(old.Invite.Id))!.ConsumedAt.Should().BeNull();
            var after = (await Read<Member>(member.ToString("N")))!;
            after.IsActive.Should().Be(current!.IsActive);
            after.OwnerTenantId.Should().Be(current.OwnerTenantId);
            after.ExternalUserId.Should().Be(current.ExternalUserId);
            after.AuthNotBefore.Should().Be(current.AuthNotBefore);
        }
    }

    [Fact]
    public async Task Duplicate_resend_preserves_winning_invite_and_rolls_back_losing_retirement()
    {
        var member = Guid.NewGuid();
        const string external = "member'; THROW 'injected'; --";
        var user = User(member);
        user.OwnerTenantId = Link(_tenant);
        user.ExternalUserId = external;
        await Write(SurrealWriter.Create(user));
        var first = Delivery(member, external);
        await _store.SaveDeliveryAsync(first.Row, null, first.Invite, member, Link(_tenant), null, _authority, default);
        var previous = (await Read<MemberHandoff>(first.Row.Id))!;
        var winner = Delivery(member, external, previous);
        var loser = Delivery(member, external, previous);
        await _store.SaveDeliveryAsync(winner.Row, previous, winner.Invite, member, Link(_tenant), null, _authority, default);
        var duplicate = () => _store.SaveDeliveryAsync(loser.Row, previous, loser.Invite, member,
            Link(_tenant), null, _authority, default);
        await duplicate.Should().ThrowAsync<SurrealStatementException>().WithMessage("*HANDOFF_CHANGED*");

        var saved = (await Read<MemberHandoff>(first.Row.Id))!;
        saved.Version.Should().Be(1);
        saved.Descriptor.Should().Be(first.Row.Descriptor);
        saved.ExternalSubject.Should().Be(external);
        saved.TokenDigest.Should().Be(winner.Row.TokenDigest);
        saved.ProtectedDelivery.Should().Be(winner.Row.ProtectedDelivery);
        (await _database.Executor.QueryAsync<MemberHandoff>(SurrealQuery<MemberHandoff>.From())).Should().ContainSingle();
        (await Read<WalletAuthClaimToken>(first.Invite.Id))!.ConsumedAt.Should().NotBeNull();
        (await Read<WalletAuthClaimToken>(winner.Invite.Id))!.ConsumedAt.Should().BeNull();
        (await Read<WalletAuthClaimToken>(loser.Invite.Id)).Should().BeNull();
        (await _database.Executor.QueryAsync<WalletAuthClaimToken>(SurrealQuery<WalletAuthClaimToken>.From()))
            .Should().HaveCount(2);
    }

    private (MemberHandoff Row, WalletAuthClaimToken Invite) Delivery(Guid member, string external, MemberHandoff? previous = null)
    {
        var id = previous?.Id ?? Guid.NewGuid().ToString("N");
        var tokenDigest = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var expires = previous?.ExpiresAt ?? now.AddHours(72);
        var row = new MemberHandoff
        {
            Id = id, TenantId = Link(_tenant), TargetUserId = Link(member), ExternalSubject = external,
            Descriptor = previous?.Descriptor ?? JsonSerializer.Serialize(new AZOA.WebAPI.Models.MemberHandoffDescriptor(
                Guid.ParseExact(id, "N"), _tenant, "Fixture platform", member, "custodial",
                ["kyc:read", "kyc:submit", "units:issue", "swap:sign", "transfer:sign"], now.AddMonths(12),
                "https://app.example/portfolio/withdraw", expires), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            DeliveryEmailDigest = previous?.DeliveryEmailDigest ?? "fixture-recipient-digest",
            ProtectedDelivery = "opaque-protected-fixture-" + Guid.NewGuid().ToString("N"),
            TokenDigest = tokenDigest, Status = "pending", ExpiresAt = expires,
            Version = previous is null ? 0 : previous.Version + 1, UpdatedAt = now,
        };
        return (row, new WalletAuthClaimToken
        {
            Id = Guid.NewGuid().ToString("N"), Token = tokenDigest, TargetUserId = Link(member), TenantId = Link(_tenant),
            ExpiresAt = expires, CreatedAt = now,
        });
    }

    private static Member User(Guid id) => new()
    {
        Id = id.ToString("N"), Username = "fixture-" + id.ToString("N"), Email = id.ToString("N") + "@example.test",
        PasswordHash = "fixture-not-a-login-secret", IsActive = true, CreatedDate = DateTimeOffset.UtcNow,
    };
    private static string Link(Guid id) => SurrealLink.ToLink("user", id.ToString("N"))!;
    private Task<T?> Read<T>(string id) where T : class, ISurrealRecord, new()
        => _database.Executor.QuerySingleAsync<T>(SurrealQuery<T>.Key(id));
    private async Task Write(SurrealQuery query) => (await _database.Executor.ExecuteAsync(query)).EnsureAllOk();
}
