using System.Text.Json;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Json;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Providers.Stores.Surreal;

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

    public async Task<OASISResult<IHolon>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  HolonPoco.HolonTable)
                .WithParam("_id", ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<HolonPoco>(q, ct);
            return new OASISResult<IHolon>
            {
                IsError = row == null,
                Message = row == null ? "Holon not found." : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IHolon>().CaptureException(ex,
                $"SurrealHolonStore.GetByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<IEnumerable<IHolon>>> QueryAsync(
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
                    var avatarIdStr = ToSurrealId(query.AvatarId.Value);
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
                    var parentIdStr = ToSurrealId(query.ParentHolonId.Value);
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

            return new OASISResult<IEnumerable<IHolon>>
            {
                Result  = rows.Select(FromPoco).ToList<IHolon>(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IEnumerable<IHolon>>().CaptureException(ex,
                $"SurrealHolonStore.QueryAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<IHolon>> UpsertAsync(IHolon holon, CancellationToken ct = default)
    {
        try
        {
            if (holon.Id == Guid.Empty)
                holon.Id = Guid.NewGuid();

            var poco   = ToPoco(holon);
            var surrId = poco.Id;

            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    HolonPoco.HolonTable)
                .WithParam("_id",   surrId)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved  = resp.GetValues<HolonPoco>(0).FirstOrDefault();
            var result = saved is not null ? FromPoco(saved) : holon;

            return new OASISResult<IHolon> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new OASISResult<IHolon>().CaptureException(ex,
                $"SurrealHolonStore.UpsertAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var checkQ = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  HolonPoco.HolonTable)
                .WithParam("_id", ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<HolonPoco>(checkQ, ct);
            if (existing == null)
                return new OASISResult<bool> { IsError = true, Message = "Holon not found.", Result = false };

            var q = SurrealQuery
                .Of("DELETE type::record($_t, $_id)")
                .WithParam("_t",  HolonPoco.HolonTable)
                .WithParam("_id", ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new OASISResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new OASISResult<bool>().CaptureException(ex,
                $"SurrealHolonStore.DeleteAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static string ToSurrealId(Guid id)
        => id.ToString("N").ToLowerInvariant();

    private static Guid FromSurrealId(string id)
        => Guid.ParseExact(id, "N");

    private static HolonPoco ToPoco(IHolon h)
    {
        // Serialize Metadata (Dictionary<string,string>) to JsonElement? for option<object>.
        JsonElement? metadataJson = null;
        if (h.Metadata is { Count: > 0 })
        {
            var raw = JsonSerializer.Serialize(h.Metadata, SurrealJsonOptions.Default);
            using var doc = JsonDocument.Parse(raw);
            metadataJson = doc.RootElement.Clone();
        }

        // Serialize PeerHolonIds (List<Guid>) as a JSON array of "N"-format strings for option<array>.
        JsonElement? peerIdsJson = null;
        if (h.PeerHolonIds is { Count: > 0 })
        {
            var strs = h.PeerHolonIds.Select(ToSurrealId).ToList();
            var raw  = JsonSerializer.Serialize(strs, SurrealJsonOptions.Default);
            using var doc = JsonDocument.Parse(raw);
            peerIdsJson = doc.RootElement.Clone();
        }

        return new HolonPoco
        {
            Id             = ToSurrealId(h.Id),
            Name           = h.Name,
            Description    = h.Description,
            ParentHolonId  = h.ParentHolonId.HasValue ? ToSurrealId(h.ParentHolonId.Value) : null,
            AvatarId       = h.AvatarId.HasValue ? ToSurrealId(h.AvatarId.Value) : null,
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
            IsActive       = h.IsActive
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
                    peerHolonIds = strs.Select(FromSurrealId).ToList();
            }
            catch { /* best-effort */ }
        }

        return new Holon
        {
            Id            = FromSurrealId(p.Id),
            Name          = p.Name,
            Description   = p.Description,
            ParentHolonId = p.ParentHolonId is not null ? FromSurrealId(p.ParentHolonId) : null,
            AvatarId      = p.AvatarId is not null ? FromSurrealId(p.AvatarId) : null,
            ProviderName  = p.ProviderName,
            ChainId       = p.ChainId,
            AssetType     = p.AssetType,
            TokenId       = p.TokenId,
            Metadata      = metadata,
            PeerHolonIds  = peerHolonIds,
            CreatedDate   = p.CreatedDate.UtcDateTime,
            ModifiedDate  = p.ModifiedDate?.UtcDateTime,
            IsActive      = p.IsActive
        };
    }

    // ── Inline POCO ───────────────────────────────────────────────────────────

    private sealed class HolonPoco : ISurrealRecord
    {
        public const string HolonTable = "holon";

        public string SchemaName => HolonTable;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("parent_holon_id")]
        public string? ParentHolonId { get; set; }

        [JsonPropertyName("avatar_id")]
        public string? AvatarId { get; set; }

        [JsonPropertyName("provider_name")]
        public string ProviderName { get; set; } = string.Empty;

        [JsonPropertyName("chain_id")]
        public string? ChainId { get; set; }

        [JsonPropertyName("asset_type")]
        public string? AssetType { get; set; }

        [JsonPropertyName("token_id")]
        public string? TokenId { get; set; }

        [JsonPropertyName("metadata")]
        public JsonElement? Metadata { get; set; }

        [JsonPropertyName("peer_holon_ids")]
        public JsonElement? PeerHolonIds { get; set; }

        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [JsonPropertyName("modified_date")]
        public DateTimeOffset? ModifiedDate { get; set; }

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;
    }
}
