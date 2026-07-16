using System.Text.Json;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Json;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using HolonRecord = AZOA.WebAPI.Persistence.SurrealDb.Models.Holon;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>SurrealDB holon persistence; see <c>Providers/Stores/Surreal/AGENTS.md</c>.</summary>
public sealed class SurrealHolonStore : IHolonStore
{
    private readonly ISurrealExecutor _executor;

    public SurrealHolonStore(ISurrealExecutor executor)
    {
        _executor = executor;
    }

    // ── IHolonStore ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<AZOAResult<IHolon>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var q = SurrealQuery<HolonPoco>.Key(SurrealId.ToSurrealId(id));
        var row = await _executor.QuerySingleAsync<HolonPoco>(q, ct);
        return row is null
            ? AZOAResult<IHolon>.Failure("Holon not found.")
            : AZOAResult<IHolon>.Success(FromPoco(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<IHolon>>> QueryAsync(
        HolonQueryRequest? query = null, CancellationToken ct = default)
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
            // Build a typed query with AND-combined server-side predicates.
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

            if (!string.IsNullOrEmpty(query.Name))
            {
                var name = query.Name;
                builder = builder.Where(h => h.Name.Contains(name));
                hasDbFilter = true;
            }

            // An empty filter object still means all records.
            if (hasDbFilter)
            {
                rows = (await _executor.QueryAsync<HolonPoco>(builder, ct)).ToList();
            }
            else
            {
                var allQ = SurrealQuery.SelectAll(HolonPoco.HolonTable);
                rows = (await _executor.QueryAsync<HolonPoco>(allQ, ct)).ToList();
            }

        }

        return new AZOAResult<IEnumerable<IHolon>>
        {
            Result = rows.Select(FromPoco).ToList<IHolon>(),
            Message = "Success"
        };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IHolon>> UpsertAsync(IHolon holon, CancellationToken ct = default)
    {
        if (holon.Id == Guid.Empty)
            holon.Id = Guid.NewGuid();

        var poco = ToPoco(holon);

        // Coercion-safe SET-based UPSERT (SurrealWriter): keeps `table:id`-shaped
        // string columns as strings (3.x would otherwise coerce them to record
        // ids), type::decimal-wraps decimals, and omits null option<> fields.
        var q = SurrealWriter.Upsert(poco);

        var resp = await _executor.ExecuteAsync(q, ct);
        resp.EnsureAllOk();

        var saved = resp.GetValues<HolonPoco>(0).FirstOrDefault();
        var result = saved is not null ? FromPoco(saved) : holon;

        return AZOAResult<IHolon>.Success(result, "Saved.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var checkQ = SurrealQuery<HolonPoco>.Key(SurrealId.ToSurrealId(id));
        var existing = await _executor.QuerySingleAsync<HolonPoco>(checkQ, ct);
        if (existing == null)
            return AZOAResult<bool>.Failure("Holon not found.", false);

        // raw: typed delete is awaiting SurrealForge.Client 0.4 publication; expires 2026-08-31.
        var q = SurrealQuery
            .Of("DELETE type::record($_t, $_id)")
            .WithParam("_t", HolonPoco.HolonTable)
            .WithParam("_id", SurrealId.ToSurrealId(id));
        var response = await _executor.ExecuteAsync(q, ct);
        response.EnsureAllOk();

        return AZOAResult<bool>.Success(true, "Deleted.");
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> TryReserveNftTransferAsync(
        Guid holonId,
        Guid sourceAvatarId,
        Guid targetAvatarId,
        string settlementKey,
        CancellationToken ct = default)
    {
        if (holonId == Guid.Empty || sourceAvatarId == Guid.Empty || targetAvatarId == Guid.Empty)
            return AZOAResult<bool>.Failure("Transfer reservation ids must be non-empty.", false);
        if (sourceAvatarId == targetAvatarId)
            return AZOAResult<bool>.Failure("Transfer source and target avatars must differ.", false);
        if (string.IsNullOrWhiteSpace(settlementKey))
            return AZOAResult<bool>.Failure("Transfer settlement key is required.", false);

        var key = settlementKey.Trim();
        // raw: typed conditional update is awaiting SurrealForge.Client 0.4 publication; expires 2026-08-31.
        var q = SurrealQuery
            .Of("UPDATE ONLY type::record($_t, $_id) SET transfer_reservation_key = type::string($_key), transfer_target_avatar_id = type::record($_avatar_t, $_target), transfer_reserved_at = (IF transfer_reservation_key != NONE THEN transfer_reserved_at ELSE $_now END) WHERE asset_type = $_nft AND avatar_id = type::record($_avatar_t, $_source) AND (last_transfer_settlement_key = NONE OR last_transfer_settlement_key != type::string($_key)) AND (transfer_reservation_key = NONE OR (transfer_reservation_key = type::string($_key) AND transfer_target_avatar_id = type::record($_avatar_t, $_target))) RETURN AFTER")
            .WithParam("_t", HolonPoco.HolonTable)
            .WithParam("_id", SurrealId.ToSurrealId(holonId))
            .WithParam("_key", key)
            .WithParam("_avatar_t", "avatar")
            .WithParam("_target", SurrealId.ToSurrealId(targetAvatarId))
            .WithParam("_source", SurrealId.ToSurrealId(sourceAvatarId))
            .WithParam("_nft", "NFT")
            .WithParam("_now", DateTimeOffset.UtcNow);
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var attempt = await _executor.ExecuteAsync(q, ct);
            attempt.EnsureAllOk();
            return attempt;
        }, ct);
        if (response[0].AffectedCount() == 1)
            return AZOAResult<bool>.Success(true, "Transfer reserved.");

        var state = await ReadTransferStateAsync(holonId, ct);
        var alreadyFinalized = state is not null
            && string.Equals(state.LastTransferSettlementKey, key, StringComparison.Ordinal)
            && string.Equals(state.AvatarId, AvatarLink(targetAvatarId), StringComparison.Ordinal);
        return alreadyFinalized
            ? AZOAResult<bool>.Success(true, "Transfer already finalized.")
            : AZOAResult<bool>.Success(false, "NFT transfer reservation conflict.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> FinalizeReservedNftTransferAsync(
        Guid holonId,
        Guid sourceAvatarId,
        Guid targetAvatarId,
        string settlementKey,
        CancellationToken ct = default)
    {
        if (holonId == Guid.Empty || sourceAvatarId == Guid.Empty || targetAvatarId == Guid.Empty)
            return AZOAResult<bool>.Failure("Transfer finalization ids must be non-empty.", false);
        if (sourceAvatarId == targetAvatarId)
            return AZOAResult<bool>.Failure("Transfer source and target avatars must differ.", false);
        if (string.IsNullOrWhiteSpace(settlementKey))
            return AZOAResult<bool>.Failure("Transfer settlement key is required.", false);

        var key = settlementKey.Trim();
        // raw: typed conditional update is awaiting SurrealForge.Client 0.4 publication; expires 2026-08-31.
        var q = SurrealQuery
            .Of("UPDATE ONLY type::record($_t, $_id) SET avatar_id = type::record($_avatar_t, $_target), modified_date = $_now, last_transfer_settlement_key = type::string($_key), transfer_reservation_key = NONE, transfer_target_avatar_id = NONE, transfer_reserved_at = NONE WHERE asset_type = $_nft AND avatar_id = type::record($_avatar_t, $_source) AND transfer_reservation_key = type::string($_key) AND transfer_target_avatar_id = type::record($_avatar_t, $_target) RETURN AFTER")
            .WithParam("_t", HolonPoco.HolonTable)
            .WithParam("_id", SurrealId.ToSurrealId(holonId))
            .WithParam("_key", key)
            .WithParam("_avatar_t", "avatar")
            .WithParam("_target", SurrealId.ToSurrealId(targetAvatarId))
            .WithParam("_source", SurrealId.ToSurrealId(sourceAvatarId))
            .WithParam("_nft", "NFT")
            .WithParam("_now", DateTimeOffset.UtcNow);
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var attempt = await _executor.ExecuteAsync(q, ct);
            attempt.EnsureAllOk();
            return attempt;
        }, ct);
        if (response[0].AffectedCount() == 1)
            return AZOAResult<bool>.Success(true, "Transfer finalized.");

        var state = await ReadTransferStateAsync(holonId, ct);
        var alreadyFinalized = state is not null
            && string.Equals(state.LastTransferSettlementKey, key, StringComparison.Ordinal)
            && string.Equals(state.AvatarId, AvatarLink(targetAvatarId), StringComparison.Ordinal)
            && state.TransferReservationKey is null;
        return alreadyFinalized
            ? AZOAResult<bool>.Success(true, "Transfer already finalized.")
            : AZOAResult<bool>.Success(false, "NFT transfer finalization conflict.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> ReleaseNftTransferReservationAsync(
        Guid holonId,
        Guid sourceAvatarId,
        string settlementKey,
        CancellationToken ct = default)
    {
        if (holonId == Guid.Empty || sourceAvatarId == Guid.Empty)
            return AZOAResult<bool>.Failure("Transfer release ids must be non-empty.", false);
        if (string.IsNullOrWhiteSpace(settlementKey))
            return AZOAResult<bool>.Failure("Transfer settlement key is required.", false);

        var key = settlementKey.Trim();
        // raw: typed conditional update is awaiting SurrealForge.Client 0.4 publication; expires 2026-08-31.
        var q = SurrealQuery
            .Of("UPDATE ONLY type::record($_t, $_id) SET transfer_reservation_key = NONE, transfer_target_avatar_id = NONE, transfer_reserved_at = NONE WHERE avatar_id = type::record($_avatar_t, $_source) AND transfer_reservation_key = type::string($_key) RETURN AFTER")
            .WithParam("_t", HolonPoco.HolonTable)
            .WithParam("_id", SurrealId.ToSurrealId(holonId))
            .WithParam("_key", key)
            .WithParam("_avatar_t", "avatar")
            .WithParam("_source", SurrealId.ToSurrealId(sourceAvatarId));
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var attempt = await _executor.ExecuteAsync(q, ct);
            attempt.EnsureAllOk();
            return attempt;
        }, ct);
        if (response[0].AffectedCount() == 1)
            return AZOAResult<bool>.Success(true, "Transfer reservation released.");

        var state = await ReadTransferStateAsync(holonId, ct);
        var alreadyReleased = state is not null
            && string.Equals(state.AvatarId, AvatarLink(sourceAvatarId), StringComparison.Ordinal)
            && state.TransferReservationKey is null;
        return alreadyReleased
            ? AZOAResult<bool>.Success(true, "Transfer reservation already released.")
            : AZOAResult<bool>.Success(false, "NFT transfer reservation release conflict.");
    }

    private async Task<TransferStateProjection?> ReadTransferStateAsync(Guid holonId, CancellationToken ct)
    {
        var q = SurrealQuery<HolonRecord>
            .Key(SurrealId.ToSurrealId(holonId))
            .Select(h => new
            {
                h.AvatarId,
                h.TransferReservationKey,
                h.TransferTargetAvatarId,
                h.LastTransferSettlementKey,
            });
        return await _executor.QuerySingleAsync<TransferStateProjection>(q, ct);
    }

    private static string AvatarLink(Guid avatarId)
        => SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(avatarId)) ?? string.Empty;

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
            Id = SurrealId.ToSurrealId(h.Id),
            Name = h.Name,
            Description = h.Description,
            ParentHolonId = h.ParentHolonId.HasValue ? SurrealLink.ToLink(HolonPoco.HolonTable, SurrealId.ToSurrealId(h.ParentHolonId.Value)) : null,
            AvatarId = h.AvatarId.HasValue ? SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(h.AvatarId.Value)) : null,
            ProviderName = h.ProviderName,
            ChainId = h.ChainId,
            AssetType = h.AssetType,
            TokenId = h.TokenId,
            Metadata = metadataJson,
            PeerHolonIds = peerIdsJson,
            CreatedDate = new DateTimeOffset(DateTime.SpecifyKind(h.CreatedDate, DateTimeKind.Utc)),
            ModifiedDate = h.ModifiedDate.HasValue
                             ? new DateTimeOffset(DateTime.SpecifyKind(h.ModifiedDate.Value, DateTimeKind.Utc))
                             : null,
            IsActive = h.IsActive,
            SourceHolonId = h.SourceHolonId.HasValue ? SurrealLink.ToLink(HolonPoco.HolonTable, SurrealId.ToSurrealId(h.SourceHolonId.Value)) : null,
            OriginAvatarId = h.OriginAvatarId.HasValue ? SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(h.OriginAvatarId.Value)) : null,
            IsPublic = h.IsPublic
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
            catch (JsonException)
            {
                // Optional legacy metadata is best-effort; see the directory guide.
            }
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
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                // Optional legacy links are best-effort; see the directory guide.
            }
        }

        return new Holon
        {
            Id = SurrealId.FromSurrealId(p.Id),
            Name = p.Name,
            Description = p.Description,
            ParentHolonId = p.ParentHolonId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(p.ParentHolonId)!) : null,
            AvatarId = p.AvatarId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(p.AvatarId)!) : null,
            ProviderName = p.ProviderName,
            ChainId = p.ChainId,
            AssetType = p.AssetType,
            TokenId = p.TokenId,
            Metadata = metadata,
            PeerHolonIds = peerHolonIds,
            CreatedDate = p.CreatedDate.UtcDateTime,
            ModifiedDate = p.ModifiedDate?.UtcDateTime,
            IsActive = p.IsActive,
            SourceHolonId = p.SourceHolonId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(p.SourceHolonId)!) : null,
            OriginAvatarId = p.OriginAvatarId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(p.OriginAvatarId)!) : null,
            IsPublic = p.IsPublic
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

    private sealed class TransferStateProjection
    {
        [JsonPropertyName("avatar_id")]
        public string? AvatarId { get; set; }

        [JsonPropertyName("transfer_reservation_key")]
        public string? TransferReservationKey { get; set; }

        [JsonPropertyName("transfer_target_avatar_id")]
        public string? TransferTargetAvatarId { get; set; }

        [JsonPropertyName("last_transfer_settlement_key")]
        public string? LastTransferSettlementKey { get; set; }
    }
}
