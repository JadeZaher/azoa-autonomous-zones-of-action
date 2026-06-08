using System.Text.Json;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client.Json;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Providers.Stores.Surreal;

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

    public async Task<OASISResult<ISTARODK>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectById(StarRecord.StarTable, ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<StarRecord>(q, ct);
            return new OASISResult<ISTARODK>
            {
                IsError = row == null,
                Message = row == null ? "STAR ODK not found." : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<ISTARODK>().CaptureException(ex, $"SurrealStarStore.GetByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<IEnumerable<ISTARODK>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectAll(StarRecord.StarTable);
            var rows = await _executor.QueryAsync<StarRecord>(q, ct);
            return new OASISResult<IEnumerable<ISTARODK>>
            {
                Result  = rows.Select(FromPoco).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IEnumerable<ISTARODK>>().CaptureException(ex, $"SurrealStarStore.GetAllAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<ISTARODK>> UpsertAsync(ISTARODK odk, CancellationToken ct = default)
    {
        try
        {
            if (odk.Id == Guid.Empty)
                odk.Id = Guid.NewGuid();

            var poco   = ToPoco(odk);
            var surrId = poco.Id;

            // UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER
            // SurrealDB upsert: creates the record if it does not exist; replaces
            // it if it does. Same pattern as SurrealWalletStore.
            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    StarRecord.StarTable)
                .WithParam("_id",   surrId)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved  = resp.GetValues<StarRecord>(0).FirstOrDefault();
            var result = saved is not null ? FromPoco(saved) : odk;

            return new OASISResult<ISTARODK> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new OASISResult<ISTARODK>().CaptureException(ex, $"SurrealStarStore.UpsertAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            // Check existence first (matches the prior EF read-before-update contract).
            var checkQ   = SurrealQuery.SelectById(StarRecord.StarTable, ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<StarRecord>(checkQ, ct);
            if (existing == null)
                return new OASISResult<bool> { IsError = true, Message = "STAR ODK not found.", Result = false };

            var q = SurrealQuery.DeleteById(StarRecord.StarTable, ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new OASISResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new OASISResult<bool>().CaptureException(ex, $"SurrealStarStore.DeleteAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static string ToSurrealId(Guid id)
        => id.ToString("N").ToLowerInvariant();

    private static Guid FromSurrealId(string id)
        => Guid.ParseExact(id, "N");

    private static StarRecord ToPoco(ISTARODK odk)
    {
        // Serialize BoundHolonIds as a JSON array of "N"-formatted id strings.
        JsonElement? boundHolonIdsJson = null;
        if (odk.BoundHolonIds.Count > 0)
        {
            var idStrings = odk.BoundHolonIds.Select(ToSurrealId).ToList();
            var raw       = JsonSerializer.Serialize(idStrings, SurrealJsonOptions.Default);
            using var doc = JsonDocument.Parse(raw);
            boundHolonIdsJson = doc.RootElement.Clone();
        }

        return new StarRecord
        {
            Id               = ToSurrealId(odk.Id),
            Name             = odk.Name,
            Description      = odk.Description,
            PublicKey        = odk.PublicKey,
            PrivateKeyHash   = odk.PrivateKeyHash,
            AvatarId         = odk.AvatarId.HasValue ? ToSurrealId(odk.AvatarId.Value) : null,
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
                    boundHolonIds = ids.Select(FromSurrealId).ToList();
            }
            catch { /* best-effort — return empty list on malformed data */ }
        }

        return new STARODK
        {
            Id               = FromSurrealId(p.Id),
            Name             = p.Name,
            Description      = p.Description,
            PublicKey        = p.PublicKey,
            PrivateKeyHash   = p.PrivateKeyHash,
            AvatarId         = p.AvatarId is not null ? FromSurrealId(p.AvatarId) : null,
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
    // TODO: replace with generated POCO when source-gen catches up to wave-2 aggregates.

    private sealed class StarRecord : Oasis.SurrealDb.Client.ISurrealRecord
    {
        public const string StarTable = "star_odk";

        public string SchemaName => StarTable;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("public_key")]
        public string? PublicKey { get; set; }

        [JsonPropertyName("private_key_hash")]
        public string? PrivateKeyHash { get; set; }

        [JsonPropertyName("avatar_id")]
        public string? AvatarId { get; set; }

        [JsonPropertyName("bound_holon_ids")]
        public JsonElement? BoundHolonIds { get; set; }

        [JsonPropertyName("target_chain")]
        public string? TargetChain { get; set; }

        [JsonPropertyName("generated_code")]
        public string? GeneratedCode { get; set; }

        [JsonPropertyName("deployment_config")]
        public string? DeploymentConfig { get; set; }

        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [JsonPropertyName("modified_date")]
        public DateTimeOffset? ModifiedDate { get; set; }

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;
    }
}
