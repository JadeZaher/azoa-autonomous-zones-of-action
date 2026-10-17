using System.Text.Json;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Json;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Core.Surreal;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IActionGraphBundleStore"/>. Maps between the legacy
/// <see cref="ActionGraphBundle"/> domain model and an inline POCO via private
/// ToPoco / FromPoco helpers.
///
/// ID encoding: <c>Guid.ToString("N").ToLowerInvariant()</c> (32-char hex,
/// no dashes). Dates are stored as DateTimeOffset with UTC kind applied.
/// BoundHolonIds is serialised as a JSON array of "N"-formatted id strings.
/// </summary>
public sealed class SurrealActionGraphBundleStore : IActionGraphBundleStore
{
    private readonly ISurrealExecutor _executor;

    public SurrealActionGraphBundleStore(ISurrealExecutor executor)
    {
        _executor = executor;
    }

    // ── IActionGraphBundleStore ────────────────────────────────────────────────────────────

    public async Task<AZOAResult<IActionGraphBundle>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectById(ActionGraphBundleRecord.ActionGraphBundleTable, SurrealId.ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<ActionGraphBundleRecord>(q, ct);
            return new AZOAResult<IActionGraphBundle>
            {
                IsError = row == null,
                Message = row == null ? "action graph bundle not found." : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IActionGraphBundle>().CaptureException(ex, $"SurrealActionGraphBundleStore.GetByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IActionGraphBundle>> GetByNameAndUserAsync(string name, Guid userId, CancellationToken ct = default)
    {
        try
        {
            // Owner-scoped name lookup. Closes the POST IDOR: only records owned
            // by the calling user can be overwritten by name collision.
            // Name comparison is case-insensitive (string::lowercase on both sides)
            // to preserve the prior CreateOrUpdateAsync semantic.
            var q = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE string::lowercase(name) = string::lowercase($_name) AND user_id = $_user LIMIT 1")
                .WithParam("_t",      ActionGraphBundleRecord.ActionGraphBundleTable)
                .WithParam("_name",   name)
                .WithParam("_user", SurrealRecordParam.Of("user", SurrealId.ToSurrealId(userId)));

            var row = await _executor.QuerySingleAsync<ActionGraphBundleRecord>(q, ct);
            return new AZOAResult<IActionGraphBundle>
            {
                IsError = false,
                Message = row == null ? "No matching action graph bundle." : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IActionGraphBundle>().CaptureException(ex, $"SurrealActionGraphBundleStore.GetByNameAndUserAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<IActionGraphBundle>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectAll(ActionGraphBundleRecord.ActionGraphBundleTable);
            var rows = await _executor.QueryAsync<ActionGraphBundleRecord>(q, ct);
            return new AZOAResult<IEnumerable<IActionGraphBundle>>
            {
                Result  = rows.Select(FromPoco).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<IActionGraphBundle>>().CaptureException(ex, $"SurrealActionGraphBundleStore.GetAllAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IActionGraphBundle>> UpsertAsync(IActionGraphBundle bundle, CancellationToken ct = default)
    {
        try
        {
            if (bundle.Id == Guid.Empty)
                bundle.Id = Guid.NewGuid();

            var poco = ToPoco(bundle);

            var q    = SurrealWriter.Upsert(poco);
            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved  = resp.GetValues<ActionGraphBundleRecord>(0).FirstOrDefault();
            var result = saved is not null ? FromPoco(saved) : bundle;

            return new AZOAResult<IActionGraphBundle> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IActionGraphBundle>().CaptureException(ex, $"SurrealActionGraphBundleStore.UpsertAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            // Check existence first (matches the prior EF read-before-update contract).
            var checkQ   = SurrealQuery.SelectById(ActionGraphBundleRecord.ActionGraphBundleTable, SurrealId.ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<ActionGraphBundleRecord>(checkQ, ct);
            if (existing == null)
                return new AZOAResult<bool> { IsError = true, Message = "action graph bundle not found.", Result = false };

            var q = SurrealQuery.DeleteById(ActionGraphBundleRecord.ActionGraphBundleTable, SurrealId.ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new AZOAResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealActionGraphBundleStore.DeleteAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────



    private static ActionGraphBundleRecord ToPoco(IActionGraphBundle bundle)
    {
        // Serialize BoundHolonIds as a JSON array of "N"-formatted id strings.
        // ALWAYS serialize (empty → `[]`) so the SET-based upsert REPLACES the
        // column; see Providers/Stores/Surreal/AGENTS.md §set-omits-null.
        var idStrings = bundle.BoundHolonIds.Select(SurrealId.ToSurrealId).ToList();
        JsonElement? boundHolonIdsJson = JsonSerializer.SerializeToElement(idStrings, SurrealJsonOptions.Default);

        return new ActionGraphBundleRecord
        {
            Id               = SurrealId.ToSurrealId(bundle.Id),
            Name             = bundle.Name,
            Description      = bundle.Description,
            PublicKey        = bundle.PublicKey,
            PrivateKeyHash   = bundle.PrivateKeyHash,
            UserId         = bundle.UserId.HasValue ? SurrealLink.ToLink("user", SurrealId.ToSurrealId(bundle.UserId.Value)) : null,
            BoundHolonIds    = boundHolonIdsJson,
            TargetChain      = bundle.TargetChain,
            GeneratedCode    = bundle.GeneratedCode,
            DeploymentConfig = bundle.DeploymentConfig,
            CreatedDate      = new DateTimeOffset(
                                   DateTime.SpecifyKind(bundle.CreatedDate, DateTimeKind.Utc)),
            ModifiedDate     = bundle.ModifiedDate.HasValue
                               ? new DateTimeOffset(
                                     DateTime.SpecifyKind(bundle.ModifiedDate.Value, DateTimeKind.Utc))
                               : null,
            IsActive         = bundle.IsActive,
            IsPublic         = bundle.IsPublic
        };
    }

    private static ActionGraphBundle FromPoco(ActionGraphBundleRecord p)
    {
        // Deserialize BoundHolonIds from the JSON array of "N"-formatted id strings.
        List<Guid> boundHolonIds = new();
        if (p.BoundHolonIds.HasValue)
        {
            try
            {
                var ids = JsonSerializer.Deserialize<List<string>>(
                    p.BoundHolonIds.Value.GetRawText(), SurrealJsonOptions.Default);
                if (ids != null)
                    boundHolonIds = ids.Select(SurrealId.FromSurrealId).ToList();
            }
            catch { /* best-effort — return empty list on malformed data */ }
        }

        return new ActionGraphBundle
        {
            Id               = SurrealId.FromSurrealId(p.Id),
            Name             = p.Name,
            Description      = p.Description,
            PublicKey        = p.PublicKey,
            PrivateKeyHash   = p.PrivateKeyHash,
            UserId         = p.UserId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(p.UserId)!) : null,
            BoundHolonIds    = boundHolonIds,
            TargetChain      = p.TargetChain,
            GeneratedCode    = p.GeneratedCode,
            DeploymentConfig = p.DeploymentConfig,
            CreatedDate      = p.CreatedDate.UtcDateTime,
            ModifiedDate     = p.ModifiedDate?.UtcDateTime,
            IsActive         = p.IsActive,
            IsPublic         = p.IsPublic
        };
    }

    // ── Inline SurrealDB record type ──────────────────────────────────────────

    private sealed class ActionGraphBundleRecord : SurrealForge.Client.ISurrealRecord
    {
        public const string ActionGraphBundleTable = "action_graph_bundle";

        public string SchemaName => ActionGraphBundleTable;

        [Id, Column(Type = "string")]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Type = "string")]
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [Column(Type = "string")]
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [Column(Type = "option<string>")]
        [JsonPropertyName("public_key")]
        public string? PublicKey { get; set; }

        [Column(Type = "option<string>")]
        [JsonPropertyName("private_key_hash")]
        public string? PrivateKeyHash { get; set; }

        [Column(Type = "option<record<user>>")]
        [JsonPropertyName("user_id")]
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.User))]
        public string? UserId { get; set; }

        [Column(Type = "option<array<string>>")]
        [JsonPropertyName("bound_holon_ids")]
        public JsonElement? BoundHolonIds { get; set; }

        [Column(Type = "option<string>")]
        [JsonPropertyName("target_chain")]
        public string? TargetChain { get; set; }

        [Column(Type = "option<string>")]
        [JsonPropertyName("generated_code")]
        public string? GeneratedCode { get; set; }

        [Column(Type = "option<string>")]
        [JsonPropertyName("deployment_config")]
        public string? DeploymentConfig { get; set; }

        [Column(Type = "datetime")]
        [ReadOnly]
        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [Column(Type = "option<datetime>")]
        [JsonPropertyName("modified_date")]
        public DateTimeOffset? ModifiedDate { get; set; }

        [Column(Type = "bool")]
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;

        [Column(Type = "bool")]
        [JsonPropertyName("is_public")]
        public bool IsPublic { get; set; }
    }
}
