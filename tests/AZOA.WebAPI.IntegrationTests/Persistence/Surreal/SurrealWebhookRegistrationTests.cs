using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core.Webhooks;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Services.Webhooks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using Registration = AZOA.WebAPI.Models.WebhookRegistration;
using Row = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistration;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealWebhookRegistrationTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealWebhookRegistrationStore _store = null!;
    private WebhookSecretProtector _secrets = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "webhook_registration", "webhook_registration_audit", "user");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
        _secrets = new(new WalletKeyService(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AZOA:WalletEncryptionKey"] = "webhook-db-test-key" }).Build()));
        var guard = new WebhookSsrfGuard();
        guard.UseResolver(_ => [IPAddress.Parse("8.8.8.8")]);
        _store = new(_executor, _secrets, guard);
    }

    public async Task DisposeAsync() { _connection?.Dispose(); await SurrealTestSchema.DropAsync(_namespace); }

    [Fact]
    public async Task Stored_secret_is_encrypted_and_reads_never_disclose_signing_material()
    {
        var command = NewRegistration();
        var saved = await _store.UpsertAsync(command);
        saved.IsError.Should().BeFalse(saved.Message);
        var row = (await _executor.QuerySingleAsync<Row>(SurrealQuery<Row>.Key(command.Id.ToString("N"))))!;
        JsonSerializer.Serialize(row).Should().NotContain(command.Secret);
        _secrets.Unprotect(command.TenantId, command.Id, 1, row.EncryptedSecret).Should().Be(command.Secret);
        var read = (await _store.GetByTenantAsync(command.TenantId)).Result!;
        read.Secret.Should().BeEmpty();
        JsonSerializer.Serialize(read).Should().NotContain(command.Secret).And.NotContain(row.EncryptedSecret);
        (await _store.GetByTenantAsync(Guid.NewGuid())).Result.Should().BeNull();
        Action swapped = () => _secrets.Unprotect(Guid.NewGuid(), command.Id, 1, row.EncryptedSecret);
        swapped.Should().Throw<CryptographicException>();
    }

    [Fact]
    public async Task Concurrent_tenant_creation_has_exactly_one_identity_and_secret()
    {
        var first = NewRegistration();
        var second = NewRegistration(); second.TenantId = first.TenantId;
        var results = await Task.WhenAll(_store.UpsertAsync(first), _store.UpsertAsync(second));
        results.Count(result => !result.IsError).Should().Be(1);
        var rows = await _executor.QueryAsync<Row>(SurrealQuery<Row>.From());
        rows.Should().ContainSingle();
        var winner = results.Single(result => !result.IsError).Result!;
        var original = winner.Id == first.Id ? first : second;
        _secrets.Unprotect(original.TenantId, original.Id, 1, rows.Single().EncryptedSecret).Should().Be(original.Secret);
    }

    [Fact]
    public async Task Secret_rotation_preserves_destination_and_stale_update_cannot_overwrite_it()
    {
        var command = NewRegistration();
        (await _store.UpsertAsync(command)).IsError.Should().BeFalse();
        var stale = (await _store.GetByTenantAsync(command.TenantId)).Result!;
        var rotation = (await _store.GetByTenantAsync(command.TenantId)).Result!;
        rotation.Secret = WebhookSecretProtector.GenerateSecret();
        var rotated = (await _store.UpsertAsync(rotation)).Result!;
        rotated.SecretRevision.Should().Be(2); rotated.DestinationRevision.Should().Be(1);
        rotated.Version.Should().Be(2);
        stale.Url = "https://different.example/hook";
        (await _store.UpsertAsync(stale)).IsError.Should().BeTrue();
        var current = (await _store.GetByTenantAsync(command.TenantId)).Result!;
        current.Url.Should().Be(command.Url);
        _secrets.Unprotect(current.TenantId, current.Id, 2, current.EncryptedSecret).Should().Be(rotation.Secret);
        current.Url = stale.Url;
        var moved = (await _store.UpsertAsync(current)).Result!;
        moved.DestinationRevision.Should().Be(2); moved.SecretRevision.Should().Be(2);
        moved.EncryptedSecret.Should().Be(current.EncryptedSecret);
    }

    [Fact]
    public async Task Concurrent_same_version_changes_do_not_merge_or_lose_winning_secret()
    {
        var command = NewRegistration();
        (await _store.UpsertAsync(command)).IsError.Should().BeFalse();
        var first = (await _store.GetByTenantAsync(command.TenantId)).Result!;
        var second = (await _store.GetByTenantAsync(command.TenantId)).Result!;
        first.Secret = WebhookSecretProtector.GenerateSecret();
        second.Url = "https://replacement.example/hook";
        var results = await Task.WhenAll(_store.UpsertAsync(first), _store.UpsertAsync(second));
        results.Count(result => !result.IsError).Should().Be(1);
        var saved = (await _store.GetByTenantAsync(command.TenantId)).Result!;
        saved.Version.Should().Be(2);
        var expected = results[0].IsError ? command.Secret : first.Secret;
        _secrets.Unprotect(saved.TenantId, saved.Id, saved.SecretRevision, saved.EncryptedSecret).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(513, false)]
    [InlineData(32, true)]
    public async Task Invalid_secret_is_typed_validation_failure_without_persistence(int length, bool whitespace)
    {
        var command = NewRegistration(); command.Secret = new string(whitespace ? ' ' : 'a', length);
        var result = await _store.UpsertAsync(command);
        result.IsError.Should().BeTrue();
        (await _store.GetByTenantAsync(command.TenantId)).Result.Should().BeNull();
    }

    [Fact]
    public async Task Managed_create_replays_receipt_without_redisclosing_or_rotating_secret()
    {
        var authority = await SeedAuthorityAsync();
        var tenant = authority.Credential.ActorUserId;
        var command = new WebhookRegistrationCommand(Guid.NewGuid(), Guid.NewGuid(), 0,
            "https://receiver.example/hook", true, false, "Configure the tenant event receiver.");
        var results = await Task.WhenAll(_store.MutateAsync(tenant, command, authority), _store.MutateAsync(tenant, command, authority));
        results.Should().OnlyContain(result => !result.IsError);
        results.Count(result => result.Result!.NewSecret is not null).Should().Be(1);
        results.Count(result => result.Result!.Replayed).Should().Be(1);
        var winner = results.Single(result => result.Result!.NewSecret is not null).Result!;
        var replay = (await _store.MutateAsync(tenant, command, authority)).Result!;
        replay.NewSecret.Should().BeNull(); replay.Receipt.Should().Be(winner.Receipt);
        var row = (await _store.GetByTenantAsync(tenant)).Result!;
        row.Version.Should().Be(1); row.SecretRevision.Should().Be(1);
        _secrets.Unprotect(tenant, command.RegistrationId, 1, row.EncryptedSecret).Should().Be(winner.NewSecret);
        var audits = await _executor.QueryAsync<AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistrationAudit>(
            SurrealQuery<AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistrationAudit>.From());
        audits.Should().ContainSingle(); JsonSerializer.Serialize(audits).Should().NotContain(winner.NewSecret!);
        (await _store.MutateAsync(tenant, command with { Url = "https://conflict.example/hook" }, authority)).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Managed_mutation_refuses_cross_tenant_and_revoked_authority_atomically()
    {
        var authority = await SeedAuthorityAsync();
        var tenant = authority.Credential.ActorUserId;
        var command = new WebhookRegistrationCommand(Guid.NewGuid(), Guid.NewGuid(), 0,
            "https://receiver.example/hook", true, false, "Configure the tenant event receiver.");
        (await _store.MutateAsync(Guid.NewGuid(), command, authority)).IsError.Should().BeTrue();
        var revoked = await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<AZOA.WebAPI.Persistence.SurrealDb.Models.User>(tenant.ToString("N"))
            .Set(user => user.IsActive, false).Build());
        revoked.EnsureAllOk();
        (await _store.MutateAsync(tenant, command, authority)).IsError.Should().BeTrue();
        (await _store.GetByTenantAsync(tenant)).Result.Should().BeNull();
        (await _executor.QueryAsync<AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistrationAudit>(
            SurrealQuery<AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistrationAudit>.From())).Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_legacy_replacement_pins_snapshot_clears_plaintext_and_rotates_once()
    {
        var authority = await SeedAuthorityAsync(); var tenant = authority.Credential.ActorUserId;
        var id = Guid.NewGuid(); var secret = "old-plaintext-must-be-removed";
        var seeded = await _executor.ExecuteAsync(SurrealWriter.Create(new Row { Id = id.ToString("N"), TenantId = $"user:{tenant:N}",
            Url = "https://receiver.example/hook", LegacySecret = secret, IsActive = true, CreatedAt = DateTimeOffset.UtcNow }));
        seeded.EnsureAllOk();
        var before = (await _store.GetByTenantAsync(tenant)).Result!;
        before.LegacyFingerprint.Should().NotBeNullOrEmpty();
        JsonSerializer.Serialize(before).Should().NotContain(secret);
        var command = new WebhookRegistrationCommand(Guid.NewGuid(), id, 0, before.Url, false, false,
            "Replace and disable legacy registration.", true, before.LegacyFingerprint);
        (await _store.MutateAsync(tenant, command with { LegacyFingerprint = "wrong" }, authority)).IsError.Should().BeTrue();
        var result = await _store.MutateAsync(tenant, command, authority);
        result.IsError.Should().BeFalse(result.Message);
        var stored = (await _executor.QuerySingleAsync<Row>(SurrealQuery<Row>.Key(id.ToString("N"))))!;
        stored.LegacySecret.Should().BeNull(); stored.Version.Should().Be(1); stored.IsActive.Should().BeFalse();
        _secrets.Unprotect(tenant, id, 1, stored.EncryptedSecret).Should().Be(result.Result!.NewSecret);
        (await _store.MutateAsync(tenant, command, authority)).Result!.NewSecret.Should().BeNull();
        (await _store.GetByTenantAsync(tenant)).Result!.LegacyFingerprint.Should().BeNull();
    }

    [Fact]
    public async Task Unchanged_blocked_destination_can_be_disabled_but_cannot_be_reenabled()
    {
        var authority = await SeedAuthorityAsync(); var tenant = authority.Credential.ActorUserId;
        var first = new WebhookRegistrationCommand(Guid.NewGuid(), Guid.NewGuid(), 0,
            "https://receiver.example/hook", true, false, "Create tenant receiver endpoint.");
        (await _store.MutateAsync(tenant, first, authority)).IsError.Should().BeFalse();
        var guard = new WebhookSsrfGuard(); guard.UseResolver(_ => [IPAddress.Parse("127.0.0.1")]);
        var blockedStore = new SurrealWebhookRegistrationStore(_executor, _secrets, guard);
        var disable = first with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, IsActive = false, Reason = "Disable blocked receiver endpoint." };
        (await blockedStore.MutateAsync(tenant, disable, authority)).IsError.Should().BeFalse();
        (await blockedStore.MutateAsync(tenant, disable with { RequestId = Guid.NewGuid(), ExpectedVersion = 2, IsActive = true }, authority)).IsError.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exact_withdrawal_and_mutation_have_one_permanent_disposition(bool withdrawFirst)
    {
        var authority = await SeedAuthorityAsync(); var tenant = authority.Credential.ActorUserId;
        var command = new WebhookRegistrationCommand(Guid.NewGuid(), Guid.NewGuid(), 0,
            "https://receiver.example/hook", true, true, "Configure tenant receiver endpoint.");
        if (withdrawFirst)
        {
            (await _store.WithdrawAsync(tenant, command, authority)).Result!.AlreadyApplied.Should().BeFalse();
            (await _store.MutateAsync(tenant, command, authority)).IsError.Should().BeTrue();
            (await _store.GetByTenantAsync(tenant)).Result.Should().BeNull();
        }
        else
        {
            (await _store.MutateAsync(tenant, command, authority)).IsError.Should().BeFalse();
            var withdrawal = await _store.WithdrawAsync(tenant, command, authority);
            withdrawal.Result!.AlreadyApplied.Should().BeTrue(); withdrawal.Result.Receipt!.Version.Should().Be(1);
        }
        var replay = await _store.WithdrawAsync(tenant, command, authority);
        replay.Result!.AlreadyApplied.Should().Be(!withdrawFirst);
        (await _store.WithdrawAsync(tenant, command with { Reason = "Conflicting withdrawal reason." }, authority)).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_withdrawal_cannot_report_success_while_delayed_mutation_applies()
    {
        var authority = await SeedAuthorityAsync(); var tenant = authority.Credential.ActorUserId;
        var command = new WebhookRegistrationCommand(Guid.NewGuid(), Guid.NewGuid(), 0,
            "https://receiver.example/hook", true, true, "Configure tenant receiver endpoint.");
        var mutation = _store.MutateAsync(tenant, command, authority);
        var withdrawal = _store.WithdrawAsync(tenant, command, authority);
        await Task.WhenAll(mutation, withdrawal);
        withdrawal.Result.IsError.Should().BeFalse(withdrawal.Result.Message);
        var row = (await _store.GetByTenantAsync(tenant)).Result;
        if (withdrawal.Result.Result!.AlreadyApplied)
        {
            mutation.Result.IsError.Should().BeFalse(); row.Should().NotBeNull();
        }
        else
        {
            mutation.Result.IsError.Should().BeTrue(); row.Should().BeNull();
        }
        var audits = await _executor.QueryAsync<AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistrationAudit>(
            SurrealQuery<AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistrationAudit>.From());
        audits.Should().ContainSingle();
    }

    private async Task<SigningConsentWitness> SeedAuthorityAsync()
    {
        var actor = Guid.NewGuid(); var now = DateTimeOffset.UtcNow.AddSeconds(-5);
        var created = await _executor.ExecuteAsync(SurrealWriter.Create(new AZOA.WebAPI.Persistence.SurrealDb.Models.User
        { Id = actor.ToString("N"), Username = actor.ToString("N"), Email = actor.ToString("N") + "@example.test", IsActive = true, CreatedDate = now }));
        created.EnsureAllOk();
        return new(1, new SigningCredentialWitness(1, "Jwt", actor, null, null, now, now.AddMinutes(10),
            null, null, [], true, TokenUse: AzoaClaims.TokenUseLogin), [AzoaScopes.TenantProvision], []);
    }

    private static Registration NewRegistration() => new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(),
        Url = "https://receiver.example/hook", Secret = WebhookSecretProtector.GenerateSecret() };
}
