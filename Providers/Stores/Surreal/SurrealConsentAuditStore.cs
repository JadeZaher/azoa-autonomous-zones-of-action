using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IConsentAuditStore"/> (AC10/L1). Append-only:
/// CREATE-per-row, no update/delete surface — an audit trail is immutable.
/// </summary>
public sealed class SurrealConsentAuditStore : IConsentAuditStore
{
    private const string Table = "consent_audit";

    private readonly ISurrealExecutor _executor;

    public SurrealConsentAuditStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<AZOAResult<bool>> AppendAsync(ConsentAuditEntry entry, CancellationToken ct = default)
    {
        try
        {
            if (entry.Id == Guid.Empty) entry.Id = Guid.NewGuid();
            var poco = FromDomain(entry);
            var q = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", poco.Id)
                .WithParam("_body", poco);
            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();
            return new AZOAResult<bool> { Result = true, Message = "Audited." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealConsentAuditStore.AppendAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<ConsentAuditEntry>>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM consent_audit WHERE tenant_id = $_tenant ORDER BY occurred_at DESC")
                .WithParam("_tenant", SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(tenantId)));
            var rows = await _executor.QueryAsync<ConsentAuditPoco>(q, ct);
            return new AZOAResult<IEnumerable<ConsentAuditEntry>>
            {
                Result = rows.Select(ToDomain).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<ConsentAuditEntry>>().CaptureException(ex, $"SurrealConsentAuditStore.ListByTenantAsync failed: {ex.Message}");
        }
    }


    private static ConsentAuditPoco FromDomain(ConsentAuditEntry e) => new()
    {
        Id         = SurrealId.ToSurrealId(e.Id),
        Action     = e.Action.ToString(),
        GrantId    = e.GrantId == Guid.Empty ? null : SurrealId.ToSurrealId(e.GrantId),
        TenantId   = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(e.TenantId)) ?? string.Empty,
        AvatarId   = e.AvatarId == Guid.Empty ? null : SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(e.AvatarId)),
        Scope      = e.Scope,
        Detail     = e.Detail,
        OccurredAt = new DateTimeOffset(DateTime.SpecifyKind(e.OccurredAt, DateTimeKind.Utc)),
    };

    private static ConsentAuditEntry ToDomain(ConsentAuditPoco p) => new()
    {
        Id         = SurrealId.FromSurrealId(p.Id),
        Action     = Enum.TryParse<ConsentAuditAction>(p.Action, ignoreCase: true, out var a) ? a : ConsentAuditAction.TenantSignDenied,
        GrantId    = string.IsNullOrEmpty(p.GrantId) ? Guid.Empty : SurrealId.FromSurrealId(p.GrantId),
        TenantId   = SurrealId.FromSurrealId(SurrealLink.FromLink(p.TenantId)!),
        AvatarId   = string.IsNullOrEmpty(p.AvatarId) ? Guid.Empty : SurrealId.FromSurrealId(SurrealLink.FromLink(p.AvatarId)!),
        Scope      = p.Scope ?? string.Empty,
        Detail     = p.Detail,
        OccurredAt = p.OccurredAt.UtcDateTime,
    };

    private sealed class ConsentAuditPoco : Azoa.SurrealDb.Client.ISurrealRecord
    {
        public string SchemaName => Table;

        [JsonPropertyName("id")]          public string Id { get; set; } = string.Empty;
        [JsonPropertyName("action")]      public string Action { get; set; } = string.Empty;
        [JsonPropertyName("grant_id")]    public string? GrantId { get; set; }
        [JsonPropertyName("tenant_id")]   public string TenantId { get; set; } = string.Empty;
        [JsonPropertyName("avatar_id")]   public string? AvatarId { get; set; }
        [JsonPropertyName("scope")]       public string? Scope { get; set; }
        [JsonPropertyName("detail")]      public string? Detail { get; set; }
        [JsonPropertyName("occurred_at")] public DateTimeOffset OccurredAt { get; set; }
    }
}
