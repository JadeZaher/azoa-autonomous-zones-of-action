using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using SurrealForge.Client.Schema;
using AZOA.WebAPI.Core.Surreal;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IActionAccessRequestStore"/>. One row per
/// (action, requester) access request. Pattern mirrors
/// <see cref="SurrealActionRunStore"/> — Guid('N') lowercase-hex record ids,
/// inline POCO (replace with generated POCO when source-gen catches up —
/// <c>AZOA.WebAPI.Persistence.SurrealDb.Models.ActionAccessRequest</c> already
/// exists), every value parameter-bound (G3 / SRDB0001). State machine +
/// idempotency invariant are enforced in the manager; see
/// <c>Persistence/SurrealDb/Models/AGENTS.md §action-access-request</c>.
/// </summary>
public sealed class SurrealActionAccessRequestStore : IActionAccessRequestStore
{
    private const string RequestTable = "action_access_request";

    private readonly ISurrealExecutor _executor;

    public SurrealActionAccessRequestStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<AZOAResult<ActionAccessRequest>> CreateAsync(
        ActionAccessRequest request, CancellationToken ct = default)
    {
        if (request is null)
            return Err<ActionAccessRequest>("CreateAsync: request must not be null.");
        if (request.Id == Guid.Empty)
            return Err<ActionAccessRequest>("CreateAsync: request.Id must not be empty.");

        try
        {
            var poco = FromDomain(request);
            var q = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    RequestTable)
                .WithParam("_id",   poco.Id)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();
            return Ok(request, "Created.");
        }
        catch (Exception ex)
        {
            return Err<ActionAccessRequest>(
                $"SurrealActionAccessRequestStore.CreateAsync({request.Id}) failed: {ex.Message}");
        }
    }

    // ── GetById ───────────────────────────────────────────────────────────────

    public async Task<AZOAResult<ActionAccessRequest>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  RequestTable)
                .WithParam("_id", SurrealId.ToSurrealId(id));

            var rows = await _executor.QueryAsync<ActionAccessRequestPoco>(q, ct);
            return rows.Count == 0
                ? Missing<ActionAccessRequest>($"ActionAccessRequest {id} not found.")
                : Ok(ToDomain(rows[0]));
        }
        catch (Exception ex)
        {
            return Err<ActionAccessRequest>(
                $"SurrealActionAccessRequestStore.GetByIdAsync({id}) failed: {ex.Message}");
        }
    }

    // ── List queries ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<IEnumerable<ActionAccessRequest>>> GetByActionAsync(
        Guid actionId, ActionAccessRequestStatus? status = null, CancellationToken ct = default)
    {
        try
        {
            var actionLink = SurrealRecordParam.Of("action", SurrealId.ToSurrealId(actionId));
            SurrealQuery q = status is { } s
                ? SurrealQuery
                    .Of("SELECT * FROM action_access_request WHERE action_id = $_qid AND status = $_status")
                    .WithParam("_qid", actionLink)
                    .WithParam("_status", s.ToString())
                : SurrealQuery
                    .Of("SELECT * FROM action_access_request WHERE action_id = $_qid")
                    .WithParam("_qid", actionLink);

            var rows = await _executor.QueryAsync<ActionAccessRequestPoco>(q, ct);
            return OkMany(rows.Select(ToDomain).ToList());
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<ActionAccessRequest>>(
                $"SurrealActionAccessRequestStore.GetByActionAsync({actionId}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<ActionAccessRequest>>> GetByRequesterAsync(
        Guid requesterUserId, ActionAccessRequestStatus? status = null, CancellationToken ct = default)
    {
        try
        {
            var userLink = SurrealRecordParam.Of("user", SurrealId.ToSurrealId(requesterUserId));
            SurrealQuery q = status is { } s
                ? SurrealQuery
                    .Of("SELECT * FROM action_access_request WHERE requester_user_id = $_aid AND status = $_status")
                    .WithParam("_aid", userLink)
                    .WithParam("_status", s.ToString())
                : SurrealQuery
                    .Of("SELECT * FROM action_access_request WHERE requester_user_id = $_aid")
                    .WithParam("_aid", userLink);

            var rows = await _executor.QueryAsync<ActionAccessRequestPoco>(q, ct);
            return OkMany(rows.Select(ToDomain).ToList());
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<ActionAccessRequest>>(
                $"SurrealActionAccessRequestStore.GetByRequesterAsync({requesterUserId}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<ActionAccessRequest>> GetPendingForActionAndRequesterAsync(
        Guid actionId, Guid requesterUserId, CancellationToken ct = default)
    {
        try
        {
            var actionLink  = SurrealRecordParam.Of("action",  SurrealId.ToSurrealId(actionId));
            var userLink = SurrealRecordParam.Of("user", SurrealId.ToSurrealId(requesterUserId));

            // ≤1-Pending idempotency: at most one non-terminal request per
            // (action, requester). LIMIT 1 is defensive — the manager upholds
            // uniqueness — so a stray duplicate cannot fault the read.
            var q = SurrealQuery
                .Of("SELECT * FROM action_access_request WHERE action_id = $_qid AND requester_user_id = $_aid AND status = $_status LIMIT 1")
                .WithParam("_qid",    actionLink)
                .WithParam("_aid",    userLink)
                .WithParam("_status", ActionAccessRequestStatus.Pending.ToString());

            var rows = await _executor.QueryAsync<ActionAccessRequestPoco>(q, ct);
            // Empty is a NON-error result (Result == null, IsError == false) so the
            // manager can distinguish "no pending exists" (open a fresh one) from a
            // store FAULT (Err below → fail closed, never a duplicate Pending).
            return rows.Count == 0
                ? Ok<ActionAccessRequest>(null!, $"No pending request for action {actionId} / requester {requesterUserId}.")
                : Ok(ToDomain(rows[0]));
        }
        catch (Exception ex)
        {
            return Err<ActionAccessRequest>(
                $"SurrealActionAccessRequestStore.GetPendingForActionAndRequesterAsync({actionId},{requesterUserId}) failed: {ex.Message}");
        }
    }

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<AZOAResult<ActionAccessRequest>> UpdateAsync(
        ActionAccessRequest request, CancellationToken ct = default)
    {
        if (request is null)
            return Err<ActionAccessRequest>("UpdateAsync: request must not be null.");
        if (request.Id == Guid.Empty)
            return Err<ActionAccessRequest>("UpdateAsync: request.Id must not be empty.");

        try
        {
            var surrealId = SurrealId.ToSurrealId(request.Id);

            // Pre-check existence so a missing id surfaces "not found" rather
            // than silently CREATE-on-UPSERT (mirrors SurrealActionRunStore).
            var existsQ = SurrealQuery
                .Of("SELECT id FROM type::record($_t, $_id)")
                .WithParam("_t",  RequestTable)
                .WithParam("_id", surrealId);

            var existing = await _executor.QueryAsync<ActionAccessRequestIdProjection>(existsQ, ct);
            if (existing.Count == 0)
                return Missing<ActionAccessRequest>($"ActionAccessRequest {request.Id} not found.");

            var poco = FromDomain(request);
            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    RequestTable)
                .WithParam("_id",   surrealId)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();
            return Ok(request, "Updated.");
        }
        catch (Exception ex)
        {
            return Err<ActionAccessRequest>(
                $"SurrealActionAccessRequestStore.UpdateAsync({request.Id}) failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static ActionAccessRequestPoco FromDomain(ActionAccessRequest r) => new()
    {
        Id                = SurrealId.ToSurrealId(r.Id),
        ActionId           = SurrealLink.ToLink("action",  SurrealId.ToSurrealId(r.ActionId))!,
        RequesterUserId = SurrealLink.ToLink("user", SurrealId.ToSurrealId(r.RequesterUserId))!,
        Status            = r.Status.ToString(),
        Message           = r.Message,
        DecisionReason    = r.DecisionReason,
        CreatedAt         = ToUtcOffset(r.CreatedAt),
        DecidedAt         = r.DecidedAt.HasValue ? ToUtcOffset(r.DecidedAt.Value) : null,
        DecidedByUserId = r.DecidedByUserId.HasValue
                            ? SurrealLink.ToLink("user", SurrealId.ToSurrealId(r.DecidedByUserId.Value))
                            : null,
    };

    private static ActionAccessRequest ToDomain(ActionAccessRequestPoco p) => new()
    {
        Id                = SurrealId.FromSurrealId(p.Id),
        ActionId           = string.IsNullOrEmpty(p.ActionId)           ? Guid.Empty : FromSurrealIdFk(SurrealLink.FromLink(p.ActionId)!),
        RequesterUserId = string.IsNullOrEmpty(p.RequesterUserId) ? Guid.Empty : FromSurrealIdFk(SurrealLink.FromLink(p.RequesterUserId)!),
        Status            = ParseStatus(p.Status),
        Message           = p.Message,
        DecisionReason    = p.DecisionReason,
        CreatedAt         = p.CreatedAt.UtcDateTime,
        DecidedAt         = p.DecidedAt?.UtcDateTime,
        DecidedByUserId = string.IsNullOrEmpty(p.DecidedByUserId) ? null : FromSurrealIdFk(SurrealLink.FromLink(p.DecidedByUserId)!),
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Guid FromSurrealIdFk(string id)
    {
        var stripped = StripIdPrefix(id);
        return Guid.TryParseExact(stripped, "N", out var g) ? g : Guid.Empty;
    }

    private static string StripIdPrefix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var colon = raw.IndexOf(':');
        if (colon < 0 || colon >= raw.Length - 1) return raw;
        return raw[(colon + 1)..].Trim('⟨', '⟩');
    }

    private static DateTimeOffset ToUtcOffset(DateTime dt)
    {
        var utc = dt.Kind switch
        {
            DateTimeKind.Utc         => dt,
            DateTimeKind.Local       => dt.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            _                        => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        };
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static ActionAccessRequestStatus ParseStatus(string? raw) =>
        Enum.TryParse<ActionAccessRequestStatus>(raw, ignoreCase: false, out var v)
            ? v
            : throw new InvalidOperationException(
                $"Unrecognised ActionAccessRequestStatus '{raw}' read from SurrealDB. " +
                "Schema ASSERT INSIDE [...] should have prevented this; refresh the schema.");

    private static AZOAResult<T> Ok<T>(T value, string msg = "Success") =>
        new() { Result = value, Message = msg };

    private static AZOAResult<IEnumerable<T>> OkMany<T>(IEnumerable<T> values, string msg = "Success") =>
        new() { Result = values, Message = msg };

    private static AZOAResult<T> Missing<T>(string msg) =>
        new() { IsError = true, Message = msg, Result = default };

    private static AZOAResult<T> Err<T>(string msg) =>
        new() { IsError = true, Message = msg, Result = default };

    // ── POCO (private — replace with generated POCO when source-gen catches up) ──

    private sealed class ActionAccessRequestPoco : ISurrealRecord
    {
        public string SchemaName => RequestTable;

        [JsonPropertyName("id")]                    public string Id { get; set; } = string.Empty;
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.ActionDefinition))] [JsonPropertyName("action_id")]              public string ActionId { get; set; } = string.Empty;
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.User))] [JsonPropertyName("requester_user_id")]   public string RequesterUserId { get; set; } = string.Empty;
        [JsonPropertyName("status")]                public string? Status { get; set; }
        [JsonPropertyName("message")]               public string? Message { get; set; }
        [JsonPropertyName("decision_reason")]       public string? DecisionReason { get; set; }
        [JsonPropertyName("created_at")]            public DateTimeOffset CreatedAt { get; set; }
        [JsonPropertyName("decided_at")]            public DateTimeOffset? DecidedAt { get; set; }
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.User))] [JsonPropertyName("decided_by_user_id")]  public string? DecidedByUserId { get; set; }
    }

    private sealed class ActionAccessRequestIdProjection
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    }
}
