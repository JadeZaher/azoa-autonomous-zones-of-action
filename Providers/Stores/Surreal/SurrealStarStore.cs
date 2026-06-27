using System.Text.Json;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Json;
using Azoa.SurrealDb.Client.Query;
using Azoa.SurrealDb.Client.Schema;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="ISTARStore"/>. Maps between the legacy
/// <see cref="STARODK"/> domain model and an inline POCO via private
/// ToPoco / FromPoco helpers.
///
/// ID encoding: <c>Guid.ToString("N").ToLowerInvariant()</c> (32-char hex,
/// no dashes). Dates are stored as DateTimeOffset with UTC kind applied.
/// BoundHolonIds is serialised as a JSON array of "N"-formatted id strings.
/// </summary>
public sealed class SurrealStarStore : ISTARStore
{
    private readonly ISurrealExecutor _executor;

    public SurrealStarStore(ISurrealExecutor executor)
    {
        _executor = executor;
    }

    // ── ISTARStore ────────────────────────────────────────────────────────────

    public async Task<AZOAResult<ISTARODK>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectById(StarRecord.StarTable, SurrealId.ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<StarRecord>(q, ct);
            return new AZOAResult<ISTARODK>
            {
                IsError = row == null,
                Message = row == null ? "STAR ODK not found." : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<ISTARODK>().CaptureException(ex, $"SurrealStarStore.GetByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<ISTARODK>> GetByNameAndAvatarAsync(string name, Guid avatarId, CancellationToken ct = default)
    {
        try
        {
            // Owner-scoped name lookup. Closes the POST IDOR: only records owned
            // by the calling avatar can be overwritten by name collision.
            // Name comparison is case-insensitive (string::lowercase on both sides)
            // to preserve the prior CreateOrUpdateAsync semantic.
            var q = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE string::lowercase(name) = string::lowercase($_name) AND avatar_id = $_avatar LIMIT 1")
                .WithParam("_t",      StarRecord.StarTable)
                .WithParam("_name",   name)
                .WithParam("_avatar", SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(avatarId)));

            var row = await _executor.QuerySingleAsync<StarRecord>(q, ct);
            return new AZOAResult<ISTARODK>
            {
                IsError = false,
                Message = row == null ? "No matching STAR ODK." : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<ISTARODK>().CaptureException(ex, $"SurrealStarStore.GetByNameAndAvatarAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<ISTARODK>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectAll(StarRecord.StarTable);
            var rows = await _executor.QueryAsync<StarRecord>(q, ct);
            return new AZOAResult<IEnumerable<ISTARODK>>
            {
                Result  = rows.Select(FromPoco).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<ISTARODK>>().CaptureException(ex, $"SurrealStarStore.GetAllAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<ISTARODK>> UpsertAsync(ISTARODK odk, CancellationToken ct = default)
    {
        try
        {
            if (odk.Id == Guid.Empty)
                odk.Id = Guid.NewGuid();

            var poco = ToPoco(odk);

            var q    = SurrealWriter.Upsert(poco);
            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved  = resp.GetValues<StarRecord>(0).FirstOrDefault();
            var result = saved is not null ? FromPoco(saved) : odk;

            return new AZOAResult<ISTARODK> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<ISTARODK>().CaptureException(ex, $"SurrealStarStore.UpsertAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            // Check existence first (matches the prior EF read-before-update contract).
            var checkQ   = SurrealQuery.SelectById(StarRecord.StarTable, SurrealId.ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<StarRecord>(checkQ, ct);
            if (existing == null)
                return new AZOAResult<bool> { IsError = true, Message = "STAR ODK not found.", Result = false };

            var q = SurrealQuery.DeleteById(StarRecord.StarTable, SurrealId.ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new AZOAResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealStarStore.DeleteAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────



    private static StarRecord ToPoco(ISTARODK odk)
    {
        // Serialize BoundHolonIds as a JSON array of "N"-formatted id strings.
        JsonElement? boundHolonIdsJson = null;
        if (odk.BoundHolonIds.Count > 0)
        {
            var idStrings = odk.BoundHolonIds.Select(SurrealId.ToSurrealId).ToList();
            var raw       = JsonSerializer.Serialize(idStrings, SurrealJsonOptions.Default);
            using var doc = JsonDocument.Parse(raw);
            boundHolonIdsJson = doc.RootElement.Clone();
        }

        return new StarRecord
        {
            Id               = SurrealId.ToSurrealId(odk.Id),
            Name             = odk.Name,
            Description      = odk.Description,
            PublicKey        = odk.PublicKey,
            PrivateKeyHash   = odk.PrivateKeyHash,
            AvatarId         = odk.AvatarId.HasValue ? SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(odk.AvatarId.Value)) : null,
            BoundHolonIds    = boundHolonIdsJson,
            TargetChain      = odk.TargetChain,
            GeneratedCode    = odk.GeneratedCode,
            DeploymentConfig = odk.DeploymentConfig,
            CreatedDate      = new DateTimeOffset(
                                   DateTime.SpecifyKind(odk.CreatedDate, DateTimeKind.Utc)),
            ModifiedDate     = odk.ModifiedDate.HasValue
                               ? new DateTimeOffset(
                                     DateTime.SpecifyKind(odk.ModifiedDate.Value, DateTimeKind.Utc))
                               : null,
            IsActive         = odk.IsActive
        };
    }

    private static STARODK FromPoco(StarRecord p)
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

        return new STARODK
        {
            Id               = SurrealId.FromSurrealId(p.Id),
            Name             = p.Name,
            Description      = p.Description,
            PublicKey        = p.PublicKey,
            PrivateKeyHash   = p.PrivateKeyHash,
            AvatarId         = p.AvatarId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(p.AvatarId)!) : null,
            BoundHolonIds    = boundHolonIds,
            TargetChain      = p.TargetChain,
            GeneratedCode    = p.GeneratedCode,
            DeploymentConfig = p.DeploymentConfig,
            CreatedDate      = p.CreatedDate.UtcDateTime,
            ModifiedDate     = p.ModifiedDate?.UtcDateTime,
            IsActive         = p.IsActive
        };
    }

    // ── Inline SurrealDB record type ──────────────────────────────────────────

    private sealed class StarRecord : Azoa.SurrealDb.Client.ISurrealRecord
    {
        public const string StarTable = "star_odk";

        public string SchemaName => StarTable;

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

        [Column(Type = "option<record<avatar>>")]
        [JsonPropertyName("avatar_id")]
        public string? AvatarId { get; set; }

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
    }
}
