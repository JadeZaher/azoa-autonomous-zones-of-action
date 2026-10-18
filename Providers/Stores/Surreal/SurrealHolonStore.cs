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
using AZOA.WebAPI.Core.Surreal;
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

            if (query.UserId.HasValue)
            {
                var userIdStr = SurrealLink.ToLink("user", SurrealId.ToSurrealId(query.UserId.Value));
                builder = builder.Where(h => h.UserId == userIdStr);
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

        var resp = await ExecuteMutationAsync(q, ct);
        resp.EnsureAllOk();

        var saved = resp.GetValues<HolonPoco>(2).FirstOrDefault();
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

        var owner = existing.UserId;
        var q = SurrealWriter.DeleteOnly<HolonPoco>(SurrealId.ToSurrealId(id)).Where(row => row.UserId == owner).Build();
        var response = await ExecuteMutationAsync(q, ct);
        return response[2].AffectedCount() == 1 ? AZOAResult<bool>.Success(true, "Deleted.")
            : AZOAResult<bool>.Failure("Holon changed before it could be deleted.", false);
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> TryReserveNftTransferAsync(
        Guid holonId,
        Guid sourceUserId,
        Guid targetUserId,
        string settlementKey,
        CancellationToken ct = default)
    {
        if (holonId == Guid.Empty || sourceUserId == Guid.Empty || targetUserId == Guid.Empty)
            return AZOAResult<bool>.Failure("Transfer reservation ids must be non-empty.", false);
        if (sourceUserId == targetUserId)
            return AZOAResult<bool>.Failure("Transfer source and target users must differ.", false);
        if (string.IsNullOrWhiteSpace(settlementKey))
            return AZOAResult<bool>.Failure("Transfer settlement key is required.", false);

        var key = settlementKey.Trim();
        var source = UserLink(sourceUserId);
        var target = UserLink(targetUserId);
        var q = SurrealWriter.UpdateOnly<HolonRecord>(SurrealId.ToSurrealId(holonId))
            .Where(row => row.AssetType == "NFT" && row.UserId == source && row.TransferReservationKey == null
                && (row.LastTransferSettlementKey == null || row.LastTransferSettlementKey != key))
            .Set(row => row.TransferReservationKey, key).Set(row => row.TransferTargetUserId, target)
            .Set(row => row.TransferReservedAt, DateTimeOffset.UtcNow).Build();
        var response = await ExecuteMutationAsync(q, ct);
        if (response[2].AffectedCount() == 1)
            return AZOAResult<bool>.Success(true, "Transfer reserved.");

        var state = await ReadTransferStateAsync(holonId, ct);
        var alreadyFinalized = state is not null
            && string.Equals(state.LastTransferSettlementKey, key, StringComparison.Ordinal)
            && string.Equals(state.UserId, UserLink(targetUserId), StringComparison.Ordinal);
        var sameReservation = state is not null && state.UserId == source
            && state.AssetType == "NFT" && state.TransferReservationKey == key && state.TransferTargetUserId == target;
        return alreadyFinalized || sameReservation
            ? AZOAResult<bool>.Success(true, "Transfer reserved or already finalized.")
            : AZOAResult<bool>.Success(false, "NFT transfer reservation conflict.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> FinalizeReservedNftTransferAsync(
        Guid holonId,
        Guid sourceUserId,
        Guid targetUserId,
        string settlementKey,
        CancellationToken ct = default)
    {
        if (holonId == Guid.Empty || sourceUserId == Guid.Empty || targetUserId == Guid.Empty)
            return AZOAResult<bool>.Failure("Transfer finalization ids must be non-empty.", false);
        if (sourceUserId == targetUserId)
            return AZOAResult<bool>.Failure("Transfer source and target users must differ.", false);
        if (string.IsNullOrWhiteSpace(settlementKey))
            return AZOAResult<bool>.Failure("Transfer settlement key is required.", false);

        var key = settlementKey.Trim();
        var source = UserLink(sourceUserId);
        var target = UserLink(targetUserId);
        var q = SurrealWriter.UpdateOnly<HolonRecord>(SurrealId.ToSurrealId(holonId))
            .Where(row => row.AssetType == "NFT" && row.UserId == source && row.TransferReservationKey == key
                && row.TransferTargetUserId == target)
            .Set(row => row.UserId, target).Set(row => row.ModifiedDate, DateTimeOffset.UtcNow)
            .Set(row => row.LastTransferSettlementKey, key).Unset(row => row.TransferReservationKey)
            .Unset(row => row.TransferTargetUserId).Unset(row => row.TransferReservedAt).Build();
        var response = await ExecuteMutationAsync(q, ct);
        if (response[2].AffectedCount() == 1)
            return AZOAResult<bool>.Success(true, "Transfer finalized.");

        var state = await ReadTransferStateAsync(holonId, ct);
        var alreadyFinalized = state is not null
            && string.Equals(state.LastTransferSettlementKey, key, StringComparison.Ordinal)
            && string.Equals(state.UserId, UserLink(targetUserId), StringComparison.Ordinal)
            && state.TransferReservationKey is null;
        return alreadyFinalized
            ? AZOAResult<bool>.Success(true, "Transfer already finalized.")
            : AZOAResult<bool>.Success(false, "NFT transfer finalization conflict.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> ReleaseNftTransferReservationAsync(
        Guid holonId,
        Guid sourceUserId,
        string settlementKey,
        CancellationToken ct = default)
    {
        if (holonId == Guid.Empty || sourceUserId == Guid.Empty)
            return AZOAResult<bool>.Failure("Transfer release ids must be non-empty.", false);
        if (string.IsNullOrWhiteSpace(settlementKey))
            return AZOAResult<bool>.Failure("Transfer settlement key is required.", false);

        var key = settlementKey.Trim();
        var source = UserLink(sourceUserId);
        var q = SurrealWriter.UpdateOnly<HolonRecord>(SurrealId.ToSurrealId(holonId))
            .Where(row => row.UserId == source && row.TransferReservationKey == key)
            .Unset(row => row.TransferReservationKey).Unset(row => row.TransferTargetUserId)
            .Unset(row => row.TransferReservedAt).Build();
        var response = await ExecuteMutationAsync(q, ct);
        if (response[2].AffectedCount() == 1)
            return AZOAResult<bool>.Success(true, "Transfer reservation released.");

        var state = await ReadTransferStateAsync(holonId, ct);
        var alreadyReleased = state is not null
            && string.Equals(state.UserId, UserLink(sourceUserId), StringComparison.Ordinal)
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
                h.UserId,
                h.AssetType,
                h.TransferReservationKey,
                h.TransferTargetUserId,
                h.LastTransferSettlementKey,
            });
        return await _executor.QuerySingleAsync<TransferStateProjection>(q, ct);
    }

    // raw: all holon mutations share the lock used to fence local graph query membership.
    private async Task<SurrealResponse> ExecuteMutationAsync(SurrealQuery mutation, CancellationToken ct)
    {
        var transaction = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"), LocalEntityMutationFence.Holons(),
            SurrealGeneratedStatementScope.Apply(mutation, "holon_mutation"), SurrealQuery.Of("COMMIT"));
        return await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await _executor.ExecuteAsync(transaction, ct);
            response.EnsureAllOk();
            return response;
        }, ct);
    }

    private static string UserLink(Guid userId)
        => SurrealLink.ToLink("user", SurrealId.ToSurrealId(userId)) ?? string.Empty;

    internal static HolonPoco ToPoco(IHolon h)
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
            UserId = h.UserId.HasValue ? SurrealLink.ToLink("user", SurrealId.ToSurrealId(h.UserId.Value)) : null,
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
            OriginUserId = h.OriginUserId.HasValue ? SurrealLink.ToLink("user", SurrealId.ToSurrealId(h.OriginUserId.Value)) : null,
            IsPublic = h.IsPublic
        };
    }

    internal static Holon FromPoco(HolonPoco p)
    {
        var metadata = p.Metadata.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(p.Metadata.Value.GetRawText(), SurrealJsonOptions.Default)
                ?? throw new JsonException("Stored holon metadata must be an object.")
            : new Dictionary<string, string>();
        var peerIds = p.PeerHolonIds.HasValue
            ? JsonSerializer.Deserialize<List<string>>(p.PeerHolonIds.Value.GetRawText(), SurrealJsonOptions.Default)
                ?? throw new JsonException("Stored holon peer identities must be an array.")
            : new List<string>();
        var peerHolonIds = peerIds.Select(id => SurrealId.FromSurrealId(SurrealLink.FromLink(id) ?? id)).ToList();

        return new Holon
        {
            Id = SurrealId.FromSurrealId(p.Id),
            Name = p.Name,
            Description = p.Description,
            ParentHolonId = p.ParentHolonId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(p.ParentHolonId)!) : null,
            UserId = p.UserId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(p.UserId)!) : null,
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
            OriginUserId = p.OriginUserId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(p.OriginUserId)!) : null,
            IsPublic = p.IsPublic
        };
    }

    // ── Inline POCO ───────────────────────────────────────────────────────────

    // Column(Type=...) attributes mirror Generated/Schemas/holon.surql so the
    // SET-based SurrealWriter classifies each field correctly: record<…> columns
    // are bound as-is (NOT type::string-wrapped), plain strings ARE wrapped so a
    // `table:id`-shaped value is not mis-coerced into a record id.
    internal sealed class HolonPoco : ISurrealRecord
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
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.Holon), Optional = true)]
        public string? ParentHolonId { get; set; }

        [Column(Type = "option<record<user>>")]
        [JsonPropertyName("user_id")]
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.User), Optional = true)]
        public string? UserId { get; set; }

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
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.Holon), Optional = true)]
        public string? SourceHolonId { get; set; }

        [Column(Type = "option<record<user>>")]
        [JsonPropertyName("origin_user_id")]
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.User), Optional = true)]
        public string? OriginUserId { get; set; }

        [Column(Type = "bool")]
        [JsonPropertyName("is_public")]
        public bool IsPublic { get; set; }
    }

    private sealed class TransferStateProjection
    {
        [JsonPropertyName("asset_type")]
        public string? AssetType { get; set; }

        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }

        [JsonPropertyName("transfer_reservation_key")]
        public string? TransferReservationKey { get; set; }

        [JsonPropertyName("transfer_target_user_id")]
        public string? TransferTargetUserId { get; set; }

        [JsonPropertyName("last_transfer_settlement_key")]
        public string? LastTransferSettlementKey { get; set; }
    }
}
