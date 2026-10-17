using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Actions;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Receipt = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionEmitReceipt;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionWebhookEvent;
using Registration = AZOA.WebAPI.Persistence.SurrealDb.Models.WebhookRegistration;
using Run = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionRun;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealActionEmitStore(ISurrealExecutor executor) : IActionEmitStore
{
    private const string Conflict = "ACTION_EMIT_AUTHORITY_CHANGED";

    /// <inheritdoc/>
    public Task<Receipt?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<Receipt>(SurrealQuery<Receipt>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<bool> HasAcceptedEventAsync(Receipt receipt, CancellationToken ct = default)
    {
        if (receipt.IsError || receipt.TenantId is null || receipt.EventId != receipt.Id || receipt.EventBodyHash is null) return false;
        var evt = await executor.QuerySingleAsync<Event>(SurrealQuery<Event>.Key(receipt.EventId), ct);
        return evt is not null && evt.ReceiptId == receipt.Id && evt.IdempotencyId == receipt.Id
            && SurrealRecordGuid.BareId(evt.TenantId) == receipt.TenantId
            && SurrealRecordGuid.BareId(evt.RunId) == receipt.RunId && SurrealRecordGuid.BareId(evt.NodeId) == receipt.NodeId
            && SurrealRecordGuid.BareId(evt.ActionId) == receipt.ActionId && evt.EventType == receipt.EventType
            && evt.PayloadJson == receipt.OutputJson && evt.RegistrationId == receipt.RegistrationId
            && evt.DestinationRevision == receipt.DestinationRevision && evt.DestinationUrl == receipt.DestinationUrl
            && evt.OccurredAt == receipt.CreatedAt && evt.BodyJson is not null
            && CanonicalJson.HashCanonical(evt.BodyJson) == receipt.EventBodyHash;
    }

    /// <inheritdoc/>
    public async Task<Receipt?> AcceptAsync(string canonicalInput, WorkflowSigningWitness authority, CancellationToken ct = default)
    {
        ActionFrozenNodeInput? input;
        try
        {
            using var document = JsonDocument.Parse(canonicalInput);
            if (CanonicalJson.Canonicalize(document.RootElement) != canonicalInput) return null;
            input = document.RootElement.Deserialize<ActionFrozenNodeInput>();
        }
        catch (JsonException) { return null; }
        var action = authority.Action;
        if (input is null || input.Version != 1 || input.NodeType != ActionNodeType.Emit || input.RunId == Guid.Empty
            || input.NodeId == Guid.Empty || input.ActorUserId == Guid.Empty || input.ActingTenantId == Guid.Empty
            || input.RunId != action.RunId || input.NodeId != action.NodeId || input.ActorUserId != action.ActorUserId
            || input.ActingTenantId != action.ActingTenantId || CanonicalJson.HashCanonical(canonicalInput) != action.IntentHash
            || authority.Credential.ActorUserId != input.ActorUserId || authority.Credential.TenantId != input.ActingTenantId)
            return null;
        var id = ActionEmitIdentity.Create(input.RunId, input.NodeId, input.ActorUserId, input.ActingTenantId);
        var previous = await GetAsync(id, ct);
        if (previous is not null) return Matches(previous, input, action.IntentHash) ? previous : null;
        if (authority.AuthorizationId is not null || !authority.RequiredScopes.SequenceEqual(new[] { AzoaScopes.ActionExecute })) return null;
        var guards = SigningAuthorityFence.ForNodeAdmission(authority, "emit_authority");
        if (guards is null) return null;
        var run = await executor.QuerySingleAsync<Run>(SurrealQuery<Run>.Key(input.RunId.ToString("N")), ct);
        if (run?.AuthenticationWitnessJson is null) return null;
        var credentialJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority.Credential));
        if (run.AuthenticationWitnessJson != credentialJson) return null;
        var now = DateTimeOffset.UtcNow;
        var receipt = new Receipt { Id = id, RunId = input.RunId.ToString("N"), NodeId = input.NodeId.ToString("N"),
            ActorId = input.ActorUserId.ToString("N"), TenantId = input.ActingTenantId?.ToString("N"),
            ActionId = SurrealRecordGuid.Parse(run.ActionId).ToString("N"), IntentHash = action.IntentHash, CreatedAt = now };
        if (input.Config.ValueKind != JsonValueKind.Object
            || !ActionNodeConfig.TryDeserialize<EmitNodeConfig>(input.Config.GetRawText(), "Emit", out var config, out _, strict: false))
        {
            receipt.IsError = true;
            receipt.Message = "Emit configuration is invalid.";
            receipt.OutputJson = "null";
        }
        else
        {
            receipt.OutputJson = config.Payload.ValueKind == JsonValueKind.Undefined ? "{}" : CanonicalJson.Canonicalize(config.Payload);
            receipt.EventType = config.EventType ?? "action.emit";
            if (string.IsNullOrWhiteSpace(receipt.EventType) || receipt.EventType.Length > 256 || receipt.EventType.Any(char.IsControl)
                || Encoding.UTF8.GetByteCount(receipt.OutputJson) > 1024 * 1024 - 4096)
            {
                receipt.IsError = true;
                receipt.Message = "Emit event name or payload exceeds its permitted bounds.";
                receipt.OutputJson = "null";
            }
        }
        Registration? registration = null;
        Event? evt = null;
        if (!receipt.IsError && input.ActingTenantId is { } tenantId)
        {
            var tenant = SurrealLink.ToLink("user", tenantId.ToString("N"))!;
            registration = (await executor.QueryAsync<Registration>(SurrealQuery<Registration>.From()
                .Where(row => row.TenantId == tenant).Limit(1), ct)).SingleOrDefault();
            if (registration is null || !registration.IsActive || registration.Version <= 0 || registration.DestinationRevision <= 0
                || registration.SecretRevision <= 0 || string.IsNullOrEmpty(registration.EncryptedSecret)) return null;
            receipt.EventId = id;
            receipt.RegistrationId = SurrealRecordGuid.Parse(registration.Id).ToString("N");
            receipt.DestinationRevision = registration.DestinationRevision;
            receipt.DestinationUrl = registration.Url;
            using var payload = JsonDocument.Parse(receipt.OutputJson);
            var body = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new { idempotencyId = id, eventType = receipt.EventType,
                runId = input.RunId.ToString("D"), nodeId = input.NodeId.ToString("D"), actionId = Guid.ParseExact(receipt.ActionId, "N").ToString("D"),
                occurredAt = now.UtcDateTime.ToString("O"), payload = payload.RootElement }));
            receipt.EventBodyHash = CanonicalJson.HashCanonical(body);
            evt = new Event { Id = id, TenantId = tenant, RunId = SurrealLink.ToLink("action_run", receipt.RunId)!,
                NodeId = SurrealLink.ToLink("action_node", receipt.NodeId)!, ActionId = run.ActionId,
                EventType = receipt.EventType, PayloadJson = receipt.OutputJson, BodyJson = body,
                OccurredAt = now, CreatedAt = now, NextAttemptAt = now, IdempotencyId = id,
                ReceiptId = id, RegistrationId = receipt.RegistrationId, DestinationRevision = receipt.DestinationRevision,
                DestinationUrl = receipt.DestinationUrl, Status = Event.DeliveryStatus.Pending };
        }
        // raw: frozen execution, original credential, current grants/destination, event and receipt commit atomically.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(ActionExecutionFence.Create(receipt.RunId, receipt.NodeId, receipt.ActorId, receipt.TenantId, receipt.IntentHash));
        statements.Add(SurrealQuery.Of("""
            IF $_effect_run.authentication_witness_json != $_emit_credential OR $_effect_run.action_id != type::record('action', $_emit_action)
                { THROW 'ACTION_EMIT_AUTHORITY_CHANGED' }
            """).WithParam("_emit_credential", credentialJson).WithParam("_emit_action", receipt.ActionId));
        statements.AddRange(guards);
        if (registration is not null)
        {
            var destination = registration;
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Registration>(receipt.RegistrationId!)
                .Where(row => row.Version == destination.Version && row.IsActive && row.TenantId == destination.TenantId
                    && row.DestinationRevision == destination.DestinationRevision && row.Url == destination.Url)
                .Set(row => row.Version, destination.Version).Build(), "emit_registration", "_emit_registration"));
            statements.Add(SurrealQuery.Of("IF $_emit_registration = NONE OR $_emit_registration = [] { THROW 'ACTION_EMIT_AUTHORITY_CHANGED' }"));
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(evt!), "emit_event"));
        }
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(receipt), "emit_receipt"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return receipt;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            var winner = await GetAsync(id, ct);
            return winner is not null && Matches(winner, input, action.IntentHash) ? winner : null;
        }
    }

    private static bool Matches(Receipt receipt, ActionFrozenNodeInput input, string hash)
        => receipt.RunId == input.RunId.ToString("N") && receipt.NodeId == input.NodeId.ToString("N")
            && receipt.ActorId == input.ActorUserId.ToString("N") && receipt.TenantId == input.ActingTenantId?.ToString("N")
            && receipt.IntentHash == hash;
}
