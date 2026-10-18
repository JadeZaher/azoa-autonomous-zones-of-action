using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using Run = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionRun;
using Execution = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNodeExecution;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionWebhookEvent;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealActionEmitTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private readonly Guid _actor = Guid.NewGuid();
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealActionEmitStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "consent_grant", "action_run", "action_node_execution",
            "action_emit_receipt", "action_webhook_event", "webhook_registration", "webhook_event_retry_audit");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new(_executor);
        await Save(new User { Id = _actor.ToString("N"), Username = _actor.ToString("N"), Email = _actor + "@test.invalid",
            PasswordHash = "test", IsActive = true });
    }

    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Fact]
    public async Task Original_dispatch_and_absent_receipt_recovery_accept_one_local_receipt()
    {
        var (input, authority) = await Command();
        var results = await Task.WhenAll(_store.AcceptAsync(input, authority), _store.AcceptAsync(input, authority));
        results.Should().OnlyContain(receipt => receipt != null && !receipt.IsError);
        results[0]!.Id.Should().Be(results[1]!.Id);
        (await _executor.QueryAsync<ActionEmitReceipt>(SurrealQuery<ActionEmitReceipt>.From())).Should().ContainSingle();
        (await _executor.QueryAsync<Event>(SurrealQuery<Event>.From())).Should().BeEmpty();
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_actor.ToString("N")).Where(seed => true)
            .Set(user => user.IsActive, false).Build())).EnsureAllOk();
        (await _store.AcceptAsync(input, authority))!.CreatedAt.Should().Be(results[0]!.CreatedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_frozen_config_has_immutable_failed_receipt_without_event(bool omitConfig)
    {
        var (input, authority) = await Command(configJson: "[]", omitConfig: omitConfig);
        var receipt = await _store.AcceptAsync(input, authority);
        receipt.Should().NotBeNull();
        receipt!.IsError.Should().BeTrue();
        receipt.EventId.Should().BeNull();
        (await _store.AcceptAsync(input, authority))!.Id.Should().Be(receipt.Id);
        (await _executor.QueryAsync<Event>(SurrealQuery<Event>.From())).Should().BeEmpty();
    }

    [Fact]
    public async Task Changed_frozen_input_or_cancelled_run_cannot_accept()
    {
        var (input, authority) = await Command();
        (await _store.AcceptAsync(input.Replace("hello", "changed", StringComparison.Ordinal), authority)).Should().BeNull();
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<Run>(authority.Action.RunId.ToString("N")).Where(seed => true)
            .Set(row => row.Status, Run.ActionRunStatusKind.Cancelled).Build())).EnsureAllOk();
        (await _store.AcceptAsync(input, authority)).Should().BeNull();
        (await _executor.QueryAsync<ActionEmitReceipt>(SurrealQuery<ActionEmitReceipt>.From())).Should().BeEmpty();
    }

    [Fact]
    public async Task Tenant_missing_registration_remains_unaccepted_then_pins_destination_once()
    {
        var tenant = Guid.NewGuid();
        await Save(new User { Id = tenant.ToString("N"), Username = tenant.ToString("N"), Email = tenant + "@test.invalid",
            PasswordHash = "test", IsActive = true });
        var grant = new ConsentGrant { Id = Guid.NewGuid().ToString("N"), GrantorUserId = "user:" + _actor.ToString("N"),
            TenantId = "user:" + tenant.ToString("N"), Scopes = AzoaScopes.ActionExecute, GrantedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await Save(grant);
        var witness = new SigningGrantWitness(Guid.ParseExact(grant.Id, "N"), AzoaScopes.ActionExecute, grant.Scopes, grant.GrantedAt, null);
        var (input, authority) = await Command(tenant, witness);
        (await _store.AcceptAsync(input, authority)).Should().BeNull();
        (await _executor.QueryAsync<ActionEmitReceipt>(SurrealQuery<ActionEmitReceipt>.From())).Should().BeEmpty();
        var registration = new WebhookRegistration { Id = Guid.NewGuid().ToString("N"), TenantId = "user:" + tenant.ToString("N"),
            Url = "https://receiver.example/events", IsActive = true, Version = 1, DestinationRevision = 1,
            SecretRevision = 1, EncryptedSecret = "protected-secret-fixture", CreatedAt = DateTimeOffset.UtcNow };
        await Save(registration);
        var receipts = await Task.WhenAll(_store.AcceptAsync(input, authority), _store.AcceptAsync(input, authority));
        receipts.Should().OnlyContain(receipt => receipt != null && !receipt.IsError);
        var evt = (await _executor.QueryAsync<Event>(SurrealQuery<Event>.From())).Should().ContainSingle().Subject;
        evt.ReceiptId.Should().Be(receipts[0]!.Id);
        evt.DestinationUrl.Should().Be(registration.Url);
        evt.RegistrationId.Should().Be(registration.Id);
        evt.BodyJson.Should().Contain("hello");
        (await _store.HasAcceptedEventAsync(receipts[0]!)).Should().BeTrue();
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<WebhookRegistration>(registration.Id).Where(seed => true)
            .Set(row => row.Version, 2L).Set(row => row.DestinationRevision, 2L)
            .Set(row => row.Url, "https://replacement.example/events").Build())).EnsureAllOk();
        (await _store.AcceptAsync(input, authority))!.DestinationUrl.Should().Be(registration.Url);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<Event>(receipts[0]!.EventId!).Where(seed => true)
            .Set(row => row.BodyJson, "{\"tampered\":true}").Build())).EnsureAllOk();
        (await _store.HasAcceptedEventAsync(receipts[0]!)).Should().BeFalse();
    }

    private async Task<(string Input, WorkflowSigningWitness Authority)> Command(Guid? tenant = null,
        SigningGrantWitness? grant = null, string configJson = "{\"Payload\":{\"message\":\"hello\"}}", bool omitConfig = false)
    {
        var run = Guid.NewGuid();
        var node = Guid.NewGuid();
        var credential = new SigningCredentialWitness(1, "Jwt", _actor, tenant, null,
            DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(1), null, null,
            [AzoaScopes.ActionExecute], tenant is null, TokenUse: tenant is null ? AzoaClaims.TokenUseLogin : AzoaClaims.TokenUseChild);
        using var config = JsonDocument.Parse(configJson);
        var frozen = new ActionFrozenNodeInput(1, run, node, ActionNodeType.Emit, _actor, tenant,
            config.RootElement.Clone(), new Dictionary<Guid, AZOA.WebAPI.Models.Actions.ActionNodeExecution>(),
            new Dictionary<Guid, AZOA.WebAPI.Models.Actions.ActionNodeExecution>());
        var input = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(frozen));
        if (omitConfig)
        {
            var missing = System.Text.Json.Nodes.JsonNode.Parse(input)!.AsObject();
            missing.Remove(nameof(ActionFrozenNodeInput.Config));
            input = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(missing));
        }
        await Save(new Run { Id = run.ToString("N"), ActionId = "action:" + Guid.NewGuid().ToString("N"),
            UserId = "user:" + _actor.ToString("N"), ActingTenantId = tenant is null ? null : "user:" + tenant.Value.ToString("N"),
            Status = Run.ActionRunStatusKind.Running, StartedAt = DateTimeOffset.UtcNow,
            AuthenticationWitnessJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(credential)) });
        await Save(new Execution { Id = Guid.NewGuid().ToString("N"), RunId = "action_run:" + run.ToString("N"),
            NodeId = "action_node:" + node.ToString("N"), State = Execution.ActionNodeState.Running,
            FrozenInput = input, StartedAt = DateTimeOffset.UtcNow });
        return (input, new(1, credential, new(run, node, _actor, tenant, CanonicalJson.HashCanonical(input)),
            [AzoaScopes.ActionExecute], grant is null ? [] : [grant]));
    }

    private async Task Save<T>(T row) where T : ISurrealRecord, new()
        => (await _executor.ExecuteAsync(SurrealWriter.Create(row))).EnsureAllOk();
}
