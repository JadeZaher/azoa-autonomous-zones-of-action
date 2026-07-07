using System.Text.Json;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Json;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IHolonStore"/>. Maps between the legacy
/// <see cref="Holon"/> domain model and an inline <see cref="HolonPoco"/>
/// using the same patterns as <see cref="SurrealNftStore"/>.
///
/// ID encoding: <c>Guid.ToString("N").ToLowerInvariant()</c> (32-char hex, no dashes).
/// Dates are stored as DateTimeOffset with UTC kind applied.
/// Metadata (Dictionary&lt;string,string&gt;) and PeerHolonIds (List&lt;Guid&gt;) are
/// serialised as JsonElement? via the Serialize→Parse→Clone pattern.
/// </summary>
public sealed class SurrealHolonStore : IHolonStore
{
    private readonly ISurrealExecutor _executor;

    public SurrealHolonStore(ISurrealExecutor executor)
    {
        _executor = executor;
    }

    // ── IHolonStore ───────────────────────────────────────────────────────────

    public async Task<AZOAResult<IHolon>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  HolonPoco.HolonTable)
                .WithParam("_id", SurrealId.ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<HolonPoco>(q, ct);
            return new AZOAResult<IHolon>
            {
                IsError = row == null,
                Message = row == null ? "Holon not found." : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IHolon>().CaptureException(ex,
                $"SurrealHolonStore.GetByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> QueryAsync(
        HolonQueryRequest? query = null, CancellationToken ct = default)
    {
        try
        {
            List<HolonPoco> rows;

            if (query == null)
            {
                // Unfiltered path — return all holons.
                var allQ = SurrealQuery.SelectAll(HolonPoco.HolonTable);
                rows = (await _executor.QueryAsync<HolonPoco>(allQ, ct)).ToList();
            }
            else
            {
                // Build typed query with AND-combined WHERE clauses.
                // Supported by the expression translator: ==, !=, bool equality.
                // Name.Contains (substring) is NOT supported by the translator;
                // it is applied as a post-filter in-memory after DB fetch.
                var builder = SurrealQuery<HolonPoco>.From();
                bool hasDbFilter = false;

                if (query.AvatarId.HasValue)
                {
                    var avatarIdStr = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(query.AvatarId.Value));
                    builder = builder.Where(h => h.AvatarId == avatarIdStr);
                    hasDbFilter = true;
                }

                if (!string.IsNullOrEmpty(query.ProviderName))
                {
                    var pn = query.ProviderName;
                    builder = builder.Where(h => h.ProviderName == pn);
                    hasDbFilter = true;
                }

                if (!string.IsNullOrEmpty(query.ChainId))
                {
                    var ci = query.ChainId;
                    builder = builder.Where(h => h.ChainId == ci);
                    hasDbFilter = true;
                }

                if (!string.IsNullOrEmpty(query.AssetType))
                {
                    var at = query.AssetType;
                    builder = builder.Where(h => h.AssetType == at);
                    hasDbFilter = true;
                }

                if (query.IsActive.HasValue)
                {
                    var ia = query.IsActive.Value;
                    builder = builder.Where(h => h.IsActive == ia);
                    hasDbFilter = true;
                }

                if (query.ParentHolonId.HasValue)
                {
                    var parentIdStr = SurrealLink.ToLink(HolonPoco.HolonTable, SurrealId.ToSurrealId(query.ParentHolonId.Value));
                    builder = builder.Where(h => h.ParentHolonId == parentIdStr);
                    hasDbFilter = true;
                }

                // Execute: if no DB filters were added we still want all records
                // (Name-only filter case), so fall back to SelectAll.
                if (hasDbFilter)
                {
                    rows = (await _executor.QueryAsync<HolonPoco>(builder, ct)).ToList();
                }
                else
                {
                    var allQ = SurrealQuery.SelectAll(HolonPoco.HolonTable);
                    rows = (await _executor.QueryAsync<HolonPoco>(allQ, ct)).ToList();
                }

                // Post-filter: Name substring match (expression translator does not
                // support string.Contains; handled client-side after DB fetch).
                if (!string.IsNullOrEmpty(query.Name))
                {
                    var name = query.Name;
                    rows = rows
                        .Where(h => h.Name.Contains(name, StringComparison.Ordinal))
                        .ToList();
                }
            }

            return new AZOAResult<IEnumerable<IHolon>>
            {
                Result  = rows.Select(FromPoco).ToList<IHolon>(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<IHolon>>().CaptureException(ex,
                $"SurrealHolonStore.QueryAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IHolon>> UpsertAsync(IHolon holon, CancellationToken ct = default)
    {
        try
        {
            if (holon.Id == Guid.Empty)
                holon.Id = Guid.NewGuid();

            var poco   = ToPoco(holon);

            // Coercion-safe SET-based UPSERT (SurrealWriter): keeps `table:id`-shaped
            // string columns as strings (3.x would otherwise coerce them to record
            // ids), type::decimal-wraps decimals, and omits null option<> fields.
            var q = SurrealWriter.Upsert(poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved  = resp.GetValues<HolonPoco>(0).FirstOrDefault();
            var result = saved is not null ? FromPoco(saved) : holon;

            return new AZOAResult<IHolon> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IHolon>().CaptureException(ex,
                $"SurrealHolonStore.UpsertAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var checkQ = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  HolonPoco.HolonTable)
                .WithParam("_id", SurrealId.ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<HolonPoco>(checkQ, ct);
            if (existing == null)
                return new AZOAResult<bool> { IsError = true, Message = "Holon not found.", Result = false };

            var q = SurrealQuery
                .Of("DELETE type::record($_t, $_id)")
                .WithParam("_t",  HolonPoco.HolonTable)
                .WithParam("_id", SurrealId.ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new AZOAResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex,
                $"SurrealHolonStore.DeleteAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static HolonPoco ToPoco(IHolon h)
    {
        // ALWAYS serialize (empty → `{}`) so the SET-based upsert REPLACES the
        // column; see Providers/Stores/Surreal/AGENTS.md §set-omits-null.
        var metadataJson = JsonSerializer.SerializeToElement(h.Metadata, SurrealJsonOptions.Default);

        // BARE hex ids for the `array<string>` field (a `table:id`-shaped element
        // is coerced to a record id by 3.x and rejected); always serialize
        // (empty → `[]`) — see AGENTS.md §set-omits-null.
        var peerIdsJson = JsonSerializer.SerializeToElement(
            h.PeerHolonIds.Select(SurrealId.ToSurrealId).ToList(), SurrealJsonOptions.Default);

        return new HolonPoco
        {
            Id             = SurrealId.ToSurrealId(h.Id),
            Name           = h.Name,
            Description    = h.Description,
            ParentHolonId  = h.ParentHolonId.HasValue ? SurrealLink.ToLink(HolonPoco.HolonTable, SurrealId.ToSurrealId(h.ParentHolonId.Value)) : null,
            AvatarId       = h.AvatarId.HasValue ? SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(h.AvatarId.Value)) : null,
            ProviderName   = h.ProviderName,
            ChainId        = h.ChainId,
            AssetType      = h.AssetType,
            TokenId        = h.TokenId,
            Metadata       = metadataJson,
            PeerHolonIds   = peerIdsJson,
            CreatedDate    = new DateTimeOffset(DateTime.SpecifyKind(h.CreatedDate, DateTimeKind.Utc)),
            ModifiedDate   = h.ModifiedDate.HasValue
                             ? new DateTimeOffset(DateTime.SpecifyKind(h.ModifiedDate.Value, DateTimeKind.Utc))
                             : null,
            IsActive       = h.IsActive,
            SourceHolonId  = h.SourceHolonId.HasValue ? SurrealLink.ToLink(HolonPoco.HolonTable, SurrealId.ToSurrealId(h.SourceHolonId.Value)) : null,
            OriginAvatarId = h.OriginAvatarId.HasValue ? SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(h.OriginAvatarId.Value)) : null,
            IsPublic       = h.IsPublic
        };
    }

    private static Holon FromPoco(HolonPoco p)
    {
        // Deserialize Metadata from JsonElement? → Dictionary<string,string>.
        Dictionary<string, string> metadata = new();
        if (p.Metadata.HasValue)
        {
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    p.Metadata.Value.GetRawText(), SurrealJsonOptions.Default);
                if (d != null) metadata = d;
            }
            catch { /* best-effort */ }
        }

        // Deserialize PeerHolonIds from JsonElement? → List<Guid>.
        List<Guid> peerHolonIds = new();
        if (p.PeerHolonIds.HasValue)
        {
            try
            {
                var strs = JsonSerializer.Deserialize<List<string>>(
                    p.PeerHolonIds.Value.GetRawText(), SurrealJsonOptions.Default);
                if (strs != null)
                    // Stored as bare hex ids (array<string>); tolerate a legacy
                    // `holon:<id>` link form by stripping the prefix if present.
                    peerHolonIds = strs.Select(s => SurrealId.FromSurrealId(SurrealLink.FromLink(s) ?? s)).ToList();
            }
            catch { /* best-effort */ }
        }

        return new Holon
        {
            Id            = SurrealId.FromSurrealId(p.Id),
            Name          = p.Name,
            Description   = p.Description,
            ParentHolonId = p.ParentHolonId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(p.ParentHolonId)!) : null,
            AvatarId      = p.AvatarId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(p.AvatarId)!) : null,
            ProviderName  = p.ProviderName,
            ChainId       = p.ChainId,
            AssetType     = p.AssetType,
            TokenId       = p.TokenId,
            Metadata      = metadata,
            PeerHolonIds  = peerHolonIds,
            CreatedDate   = p.CreatedDate.UtcDateTime,
            ModifiedDate  = p.ModifiedDate?.UtcDateTime,
            IsActive      = p.IsActive,
            SourceHolonId = p.SourceHolonId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(p.SourceHolonId)!) : null,
            OriginAvatarId = p.OriginAvatarId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(p.OriginAvatarId)!) : null,
            IsPublic      = p.IsPublic
        };
    }

    // ── Inline POCO ───────────────────────────────────────────────────────────

    // Column(Type=...) attributes mirror Generated/Schemas/holon.surql so the
    // SET-based SurrealWriter classifies each field correctly: record<…> columns
    // are bound as-is (NOT type::string-wrapped), plain strings ARE wrapped so a
    // `table:id`-shaped value is not mis-coerced into a record id.
    private sealed class HolonPoco : ISurrealRecord
    {
        public const string HolonTable = "holon";

        public string SchemaName => HolonTable;

        [Id, Column(Type = "string")]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Type = "string")]
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [Column(Type = "string")]
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [Column(Type = "option<record<holon>>")]
        [JsonPropertyName("parent_holon_id")]
        public string? ParentHolonId { get; set; }

        [Column(Type = "option<record<avatar>>")]
        [JsonPropertyName("avatar_id")]
        public string? AvatarId { get; set; }

        [Column(Type = "string")]
        [JsonPropertyName("provider_name")]
        public string ProviderName { get; set; } = string.Empty;

        [Column(Type = "option<string>")]
        [JsonPropertyName("chain_id")]
        public string? ChainId { get; set; }

        [Column(Type = "option<string>")]
        [JsonPropertyName("asset_type")]
        public string? AssetType { get; set; }

        [Column(Type = "option<string>")]
        [JsonPropertyName("token_id")]
        public string? TokenId { get; set; }

        [Column(Type = "option<object>")]
        [JsonPropertyName("metadata")]
        public JsonElement? Metadata { get; set; }

        [Column(Type = "option<array<string>>")]
        [JsonPropertyName("peer_holon_ids")]
        public JsonElement? PeerHolonIds { get; set; }

        [Column(Type = "datetime")]
        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [Column(Type = "option<datetime>")]
        [JsonPropertyName("modified_date")]
        public DateTimeOffset? ModifiedDate { get; set; }

        [Column(Type = "bool")]
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;

        [Column(Type = "option<record<holon>>")]
        [JsonPropertyName("source_holon_id")]
        public string? SourceHolonId { get; set; }

        [Column(Type = "option<record<avatar>>")]
        [JsonPropertyName("origin_avatar_id")]
        public string? OriginAvatarId { get; set; }

        [Column(Type = "bool")]
        [JsonPropertyName("is_public")]
        public bool IsPublic { get; set; }
    }
}
