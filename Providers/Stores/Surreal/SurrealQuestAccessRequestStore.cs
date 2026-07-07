using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IQuestAccessRequestStore"/>. One row per
/// (quest, requester) access request. Pattern mirrors
/// <see cref="SurrealQuestRunStore"/> — Guid('N') lowercase-hex record ids,
/// inline POCO (replace with generated POCO when source-gen catches up —
/// <c>AZOA.WebAPI.Persistence.SurrealDb.Models.QuestAccessRequest</c> already
/// exists), every value parameter-bound (G3 / SRDB0001). State machine +
/// idempotency invariant are enforced in the manager; see
/// <c>Persistence/SurrealDb/Models/AGENTS.md §quest-access-request</c>.
/// </summary>
public sealed class SurrealQuestAccessRequestStore : IQuestAccessRequestStore
{
    private const string RequestTable = "quest_access_request";

    private readonly ISurrealExecutor _executor;

    public SurrealQuestAccessRequestStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<AZOAResult<QuestAccessRequest>> CreateAsync(
        QuestAccessRequest request, CancellationToken ct = default)
    {
        if (request is null)
            return Err<QuestAccessRequest>("CreateAsync: request must not be null.");
        if (request.Id == Guid.Empty)
            return Err<QuestAccessRequest>("CreateAsync: request.Id must not be empty.");

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
            return Err<QuestAccessRequest>(
                $"SurrealQuestAccessRequestStore.CreateAsync({request.Id}) failed: {ex.Message}");
        }
    }

    // ── GetById ───────────────────────────────────────────────────────────────

    public async Task<AZOAResult<QuestAccessRequest>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  RequestTable)
                .WithParam("_id", SurrealId.ToSurrealId(id));

            var rows = await _executor.QueryAsync<QuestAccessRequestPoco>(q, ct);
            return rows.Count == 0
                ? Missing<QuestAccessRequest>($"QuestAccessRequest {id} not found.")
                : Ok(ToDomain(rows[0]));
        }
        catch (Exception ex)
        {
            return Err<QuestAccessRequest>(
                $"SurrealQuestAccessRequestStore.GetByIdAsync({id}) failed: {ex.Message}");
        }
    }

    // ── List queries ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<IEnumerable<QuestAccessRequest>>> GetByQuestAsync(
        Guid questId, QuestAccessRequestStatus? status = null, CancellationToken ct = default)
    {
        try
        {
            var questLink = SurrealLink.ToLink("quest", SurrealId.ToSurrealId(questId));
            SurrealQuery q = status is { } s
                ? SurrealQuery
                    .Of("SELECT * FROM quest_access_request WHERE quest_id = $_qid AND status = $_status")
                    .WithParam("_qid", questLink)
                    .WithParam("_status", s.ToString())
                : SurrealQuery
                    .Of("SELECT * FROM quest_access_request WHERE quest_id = $_qid")
                    .WithParam("_qid", questLink);

            var rows = await _executor.QueryAsync<QuestAccessRequestPoco>(q, ct);
            return OkMany(rows.Select(ToDomain).ToList());
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<QuestAccessRequest>>(
                $"SurrealQuestAccessRequestStore.GetByQuestAsync({questId}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<QuestAccessRequest>>> GetByRequesterAsync(
        Guid requesterAvatarId, QuestAccessRequestStatus? status = null, CancellationToken ct = default)
    {
        try
        {
            var avatarLink = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(requesterAvatarId));
            SurrealQuery q = status is { } s
                ? SurrealQuery
                    .Of("SELECT * FROM quest_access_request WHERE requester_avatar_id = $_aid AND status = $_status")
                    .WithParam("_aid", avatarLink)
                    .WithParam("_status", s.ToString())
                : SurrealQuery
                    .Of("SELECT * FROM quest_access_request WHERE requester_avatar_id = $_aid")
                    .WithParam("_aid", avatarLink);

            var rows = await _executor.QueryAsync<QuestAccessRequestPoco>(q, ct);
            return OkMany(rows.Select(ToDomain).ToList());
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<QuestAccessRequest>>(
                $"SurrealQuestAccessRequestStore.GetByRequesterAsync({requesterAvatarId}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<QuestAccessRequest>> GetPendingForQuestAndRequesterAsync(
        Guid questId, Guid requesterAvatarId, CancellationToken ct = default)
    {
        try
        {
            var questLink  = SurrealLink.ToLink("quest",  SurrealId.ToSurrealId(questId));
            var avatarLink = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(requesterAvatarId));

            // ≤1-Pending idempotency: at most one non-terminal request per
            // (quest, requester). LIMIT 1 is defensive — the manager upholds
            // uniqueness — so a stray duplicate cannot fault the read.
            var q = SurrealQuery
                .Of("SELECT * FROM quest_access_request WHERE quest_id = $_qid AND requester_avatar_id = $_aid AND status = $_status LIMIT 1")
                .WithParam("_qid",    questLink)
                .WithParam("_aid",    avatarLink)
                .WithParam("_status", QuestAccessRequestStatus.Pending.ToString());

            var rows = await _executor.QueryAsync<QuestAccessRequestPoco>(q, ct);
            // Empty is a NON-error result (Result == null, IsError == false) so the
            // manager can distinguish "no pending exists" (open a fresh one) from a
            // store FAULT (Err below → fail closed, never a duplicate Pending).
            return rows.Count == 0
                ? Ok<QuestAccessRequest>(null!, $"No pending request for quest {questId} / requester {requesterAvatarId}.")
                : Ok(ToDomain(rows[0]));
        }
        catch (Exception ex)
        {
            return Err<QuestAccessRequest>(
                $"SurrealQuestAccessRequestStore.GetPendingForQuestAndRequesterAsync({questId},{requesterAvatarId}) failed: {ex.Message}");
        }
    }

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<AZOAResult<QuestAccessRequest>> UpdateAsync(
        QuestAccessRequest request, CancellationToken ct = default)
    {
        if (request is null)
            return Err<QuestAccessRequest>("UpdateAsync: request must not be null.");
        if (request.Id == Guid.Empty)
            return Err<QuestAccessRequest>("UpdateAsync: request.Id must not be empty.");

        try
        {
            var surrealId = SurrealId.ToSurrealId(request.Id);

            // Pre-check existence so a missing id surfaces "not found" rather
            // than silently CREATE-on-UPSERT (mirrors SurrealQuestRunStore).
            var existsQ = SurrealQuery
                .Of("SELECT id FROM type::record($_t, $_id)")
                .WithParam("_t",  RequestTable)
                .WithParam("_id", surrealId);

            var existing = await _executor.QueryAsync<QuestAccessRequestIdProjection>(existsQ, ct);
            if (existing.Count == 0)
                return Missing<QuestAccessRequest>($"QuestAccessRequest {request.Id} not found.");

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
            return Err<QuestAccessRequest>(
                $"SurrealQuestAccessRequestStore.UpdateAsync({request.Id}) failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static QuestAccessRequestPoco FromDomain(QuestAccessRequest r) => new()
    {
        Id                = SurrealId.ToSurrealId(r.Id),
        QuestId           = SurrealLink.ToLink("quest",  SurrealId.ToSurrealId(r.QuestId))!,
        RequesterAvatarId = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(r.RequesterAvatarId))!,
        Status            = r.Status.ToString(),
        Message           = r.Message,
        DecisionReason    = r.DecisionReason,
        CreatedAt         = ToUtcOffset(r.CreatedAt),
        DecidedAt         = r.DecidedAt.HasValue ? ToUtcOffset(r.DecidedAt.Value) : null,
        DecidedByAvatarId = r.DecidedByAvatarId.HasValue
                            ? SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(r.DecidedByAvatarId.Value))
                            : null,
    };

    private static QuestAccessRequest ToDomain(QuestAccessRequestPoco p) => new()
    {
        Id                = SurrealId.FromSurrealId(p.Id),
        QuestId           = string.IsNullOrEmpty(p.QuestId)           ? Guid.Empty : FromSurrealIdFk(SurrealLink.FromLink(p.QuestId)!),
        RequesterAvatarId = string.IsNullOrEmpty(p.RequesterAvatarId) ? Guid.Empty : FromSurrealIdFk(SurrealLink.FromLink(p.RequesterAvatarId)!),
        Status            = ParseStatus(p.Status),
        Message           = p.Message,
        DecisionReason    = p.DecisionReason,
        CreatedAt         = p.CreatedAt.UtcDateTime,
        DecidedAt         = p.DecidedAt?.UtcDateTime,
        DecidedByAvatarId = string.IsNullOrEmpty(p.DecidedByAvatarId) ? null : FromSurrealIdFk(SurrealLink.FromLink(p.DecidedByAvatarId)!),
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

    private static QuestAccessRequestStatus ParseStatus(string? raw) =>
        Enum.TryParse<QuestAccessRequestStatus>(raw, ignoreCase: false, out var v)
            ? v
            : throw new InvalidOperationException(
                $"Unrecognised QuestAccessRequestStatus '{raw}' read from SurrealDB. " +
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

    private sealed class QuestAccessRequestPoco : ISurrealRecord
    {
        public string SchemaName => RequestTable;

        [JsonPropertyName("id")]                    public string Id { get; set; } = string.Empty;
        [JsonPropertyName("quest_id")]              public string QuestId { get; set; } = string.Empty;
        [JsonPropertyName("requester_avatar_id")]   public string RequesterAvatarId { get; set; } = string.Empty;
        [JsonPropertyName("status")]                public string? Status { get; set; }
        [JsonPropertyName("message")]               public string? Message { get; set; }
        [JsonPropertyName("decision_reason")]       public string? DecisionReason { get; set; }
        [JsonPropertyName("created_at")]            public DateTimeOffset CreatedAt { get; set; }
        [JsonPropertyName("decided_at")]            public DateTimeOffset? DecidedAt { get; set; }
        [JsonPropertyName("decided_by_avatar_id")]  public string? DecidedByAvatarId { get; set; }
    }

    private sealed class QuestAccessRequestIdProjection
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    }
}
