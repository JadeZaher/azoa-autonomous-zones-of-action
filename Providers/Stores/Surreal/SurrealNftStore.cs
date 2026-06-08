using System.Text.Json;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client.Json;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Responses;
using GeneratedNft = OASIS.WebAPI.Persistence.SurrealDb.Models.NftOwnership;

namespace OASIS.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="INftStore"/>. Maps between legacy domain
/// models (<see cref="AvatarNFT"/>, <see cref="HolonNFTBinding"/>,
/// <see cref="WalletNFTBinding"/>) and SurrealDB tables:
/// <list type="bullet">
///   <item><c>nft_ownership</c> — generated POCO (<see cref="GeneratedNft"/>)</item>
///   <item><c>holon_nft_binding</c> — no generated POCO; uses inline record type</item>
///   <item><c>wallet_nft_binding</c> — no generated POCO; uses inline record type</item>
/// </list>
///
/// AvatarNFT composite lookup: (ChainType, ContractAddress, TokenId) with
/// <c>is_current = true</c> to select live ownership rows only.
/// </summary>
public sealed class SurrealNftStore : INftStore
{
    private const string HolonBindingTable  = "holon_nft_binding";
    private const string WalletBindingTable = "wallet_nft_binding";

    private static readonly JsonSerializerOptions BindingJsonOpts = SurrealJsonOptions.Default;

    private readonly ISurrealExecutor _executor;

    public SurrealNftStore(ISurrealExecutor executor)
    {
        _executor = executor;
    }

    // ── AvatarNFT ─────────────────────────────────────────────────────────────

    public async Task<OASISResult<IAvatarNFT>> UpsertAvatarNFTAsync(
        IAvatarNFT avatarNFT, CancellationToken ct = default)
    {
        try
        {
            if (avatarNFT.Id == Guid.Empty)
                avatarNFT.Id = Guid.NewGuid();

            var poco   = ToNftPoco(avatarNFT);
            var surrId = poco.Id;

            // UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER
            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    GeneratedNft.SchemaNameConst)
                .WithParam("_id",   surrId)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved  = resp.GetValues<GeneratedNft>(0).FirstOrDefault();
            var result = saved is not null ? FromPoco(saved) : avatarNFT;

            return new OASISResult<IAvatarNFT> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new OASISResult<IAvatarNFT>().CaptureException(ex,
                $"SurrealNftStore.UpsertAvatarNFTAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<IAvatarNFT>> GetAvatarNFTByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectById(GeneratedNft.SchemaNameConst, ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<GeneratedNft>(q, ct);
            return new OASISResult<IAvatarNFT>
            {
                IsError = row == null,
                Message = row == null ? "NFT not found." : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IAvatarNFT>().CaptureException(ex,
                $"SurrealNftStore.GetAvatarNFTByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<IAvatarNFT>> GetAvatarNFTByTokenIdAsync(
        string chainType, string nftContractAddress, string tokenId, CancellationToken ct = default)
    {
        try
        {
            // Composite lookup: (chain_type, contract_address, token_id) with is_current = true
            var q = SurrealQuery<GeneratedNft>.From()
                .Where(n => n.ChainType       == chainType)
                .Where(n => n.ContractAddress == nftContractAddress)
                .Where(n => n.TokenId         == tokenId)
                .Where(n => n.IsCurrent       == true)
                .Limit(1);

            var rows = await _executor.QueryAsync<GeneratedNft>(q, ct);
            var row  = rows.Count > 0 ? rows[0] : null;

            return new OASISResult<IAvatarNFT>
            {
                IsError = row == null,
                Message = row == null ? "NFT not found." : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IAvatarNFT>().CaptureException(ex,
                $"SurrealNftStore.GetAvatarNFTByTokenIdAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<IEnumerable<IAvatarNFT>>> GetAvatarNFTsByAvatarAsync(
        Guid avatarId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery<GeneratedNft>.From()
                .Where(n => n.AvatarId == avatarId.ToString("N").ToLowerInvariant());

            var rows = await _executor.QueryAsync<GeneratedNft>(q, ct);
            return new OASISResult<IEnumerable<IAvatarNFT>>
            {
                Result  = rows.Select(FromPoco).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IEnumerable<IAvatarNFT>>().CaptureException(ex,
                $"SurrealNftStore.GetAvatarNFTsByAvatarAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<bool>> DeleteAvatarNFTAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var checkQ   = SurrealQuery.SelectById(GeneratedNft.SchemaNameConst, ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<GeneratedNft>(checkQ, ct);
            if (existing == null)
                return new OASISResult<bool> { IsError = true, Message = "NFT not found.", Result = false };

            var q = SurrealQuery.DeleteById(GeneratedNft.SchemaNameConst, ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new OASISResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new OASISResult<bool>().CaptureException(ex,
                $"SurrealNftStore.DeleteAvatarNFTAsync failed: {ex.Message}");
        }
    }

    // ── HolonNFTBinding ───────────────────────────────────────────────────────

    public async Task<OASISResult<IHolonNFTBinding>> UpsertHolonNFTBindingAsync(
        IHolonNFTBinding binding, CancellationToken ct = default)
    {
        try
        {
            if (binding.Id == Guid.Empty)
                binding.Id = Guid.NewGuid();

            var poco   = ToHolonBindingPoco(binding);
            var surrId = poco.Id;

            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    HolonBindingTable)
                .WithParam("_id",   surrId)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved  = resp.GetValues<SurrealHolonBinding>(0, BindingJsonOpts);
            var result = saved.Count > 0 ? FromSurrealHolonBinding(saved[0]) : binding;

            return new OASISResult<IHolonNFTBinding> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new OASISResult<IHolonNFTBinding>().CaptureException(ex,
                $"SurrealNftStore.UpsertHolonNFTBindingAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<IHolonNFTBinding>> GetHolonNFTBindingByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var q   = SurrealQuery.SelectById(HolonBindingTable, ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<SurrealHolonBinding>(q, ct);
            return new OASISResult<IHolonNFTBinding>
            {
                IsError = row == null,
                Message = row == null ? "Binding not found." : "Success",
                Result  = row == null ? null : FromSurrealHolonBinding(row)
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IHolonNFTBinding>().CaptureException(ex,
                $"SurrealNftStore.GetHolonNFTBindingByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<IEnumerable<IHolonNFTBinding>>> GetHolonNFTBindingsByAvatarNFTAsync(
        Guid avatarNFTId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.Of("SELECT * FROM holon_nft_binding WHERE avatar_nft_id = $avatarNftId")
                .WithParam("avatarNftId", ToSurrealId(avatarNFTId));

            var rows = await _executor.QueryAsync<SurrealHolonBinding>(q, ct);
            return new OASISResult<IEnumerable<IHolonNFTBinding>>
            {
                Result  = rows.Select(FromSurrealHolonBinding).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IEnumerable<IHolonNFTBinding>>().CaptureException(ex,
                $"SurrealNftStore.GetHolonNFTBindingsByAvatarNFTAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<bool>> DeleteHolonNFTBindingAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var checkQ   = SurrealQuery.SelectById(HolonBindingTable, ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<SurrealHolonBinding>(checkQ, ct);
            if (existing == null)
                return new OASISResult<bool> { IsError = true, Message = "Binding not found.", Result = false };

            var q = SurrealQuery.DeleteById(HolonBindingTable, ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new OASISResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new OASISResult<bool>().CaptureException(ex,
                $"SurrealNftStore.DeleteHolonNFTBindingAsync failed: {ex.Message}");
        }
    }

    // ── WalletNFTBinding ──────────────────────────────────────────────────────

    public async Task<OASISResult<IWalletNFTBinding>> UpsertWalletNFTBindingAsync(
        IWalletNFTBinding binding, CancellationToken ct = default)
    {
        try
        {
            if (binding.Id == Guid.Empty)
                binding.Id = Guid.NewGuid();

            var poco   = ToWalletBindingPoco(binding);
            var surrId = poco.Id;

            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    WalletBindingTable)
                .WithParam("_id",   surrId)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved  = resp.GetValues<SurrealWalletBinding>(0, BindingJsonOpts);
            var result = saved.Count > 0 ? FromSurrealWalletBinding(saved[0]) : binding;

            return new OASISResult<IWalletNFTBinding> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new OASISResult<IWalletNFTBinding>().CaptureException(ex,
                $"SurrealNftStore.UpsertWalletNFTBindingAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<IWalletNFTBinding>> GetWalletNFTBindingByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var q   = SurrealQuery.SelectById(WalletBindingTable, ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<SurrealWalletBinding>(q, ct);
            return new OASISResult<IWalletNFTBinding>
            {
                IsError = row == null,
                Message = row == null ? "Binding not found." : "Success",
                Result  = row == null ? null : FromSurrealWalletBinding(row)
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IWalletNFTBinding>().CaptureException(ex,
                $"SurrealNftStore.GetWalletNFTBindingByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<IEnumerable<IWalletNFTBinding>>> GetWalletNFTBindingsByAvatarNFTAsync(
        Guid avatarNFTId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.Of("SELECT * FROM wallet_nft_binding WHERE avatar_nft_id = $avatarNftId")
                .WithParam("avatarNftId", ToSurrealId(avatarNFTId));

            var rows = await _executor.QueryAsync<SurrealWalletBinding>(q, ct);
            return new OASISResult<IEnumerable<IWalletNFTBinding>>
            {
                Result  = rows.Select(FromSurrealWalletBinding).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IEnumerable<IWalletNFTBinding>>().CaptureException(ex,
                $"SurrealNftStore.GetWalletNFTBindingsByAvatarNFTAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<bool>> DeleteWalletNFTBindingAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var checkQ   = SurrealQuery.SelectById(WalletBindingTable, ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<SurrealWalletBinding>(checkQ, ct);
            if (existing == null)
                return new OASISResult<bool> { IsError = true, Message = "Binding not found.", Result = false };

            var q = SurrealQuery.DeleteById(WalletBindingTable, ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new OASISResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new OASISResult<bool>().CaptureException(ex,
                $"SurrealNftStore.DeleteWalletNFTBindingAsync failed: {ex.Message}");
        }
    }

    // ── Helpers — Id encoding ─────────────────────────────────────────────────

    private static string ToSurrealId(Guid id)
        => id.ToString("N").ToLowerInvariant();

    private static Guid FromSurrealId(string id)
        => Guid.ParseExact(id, "N");

    // ── AvatarNFT mapping ─────────────────────────────────────────────────────

    private static GeneratedNft ToNftPoco(IAvatarNFT n)
    {
        JsonElement? attributesJson = null;
        if (n.Attributes.Count > 0)
        {
            var raw = JsonSerializer.Serialize(n.Attributes, SurrealJsonOptions.Default);
            using var doc = JsonDocument.Parse(raw);
            attributesJson = doc.RootElement.Clone();
        }

        return new GeneratedNft
        {
            Id                = ToSurrealId(n.Id),
            AvatarId          = ToSurrealId(n.AvatarId),
            ChainType         = n.ChainType,
            ContractAddress   = n.NFTContractAddress,
            TokenId           = n.TokenId,
            TokenStandard     = n.TokenStandard,
            MetadataUri       = n.MetadataURI,
            ImageUri          = n.ImageURI,
            Name              = n.Name,
            Description       = n.Description,
            Attributes        = attributesJson,
            RoyaltyPercentage = n.RoyaltyPercentage,
            RoyaltyRecipient  = n.RoyaltyRecipient,
            IsSoulbound       = n.IsSoulbound,
            IsTransferable    = n.IsTransferable,
            IsCurrent         = true,
            CurrentOwner      = n.CurrentOwner,
            IsActive          = n.IsActive,
            MintedDate        = new DateTimeOffset(
                                    DateTime.SpecifyKind(n.MintedDate, DateTimeKind.Utc)),
            LastTransferDate  = n.LastTransferDate.HasValue
                                ? new DateTimeOffset(
                                      DateTime.SpecifyKind(n.LastTransferDate.Value, DateTimeKind.Utc))
                                : null
        };
    }

    private static AvatarNFT FromPoco(GeneratedNft p)
    {
        Dictionary<string, string> attributes = new();
        if (p.Attributes.HasValue)
        {
            try
            {
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    p.Attributes.Value.GetRawText(), SurrealJsonOptions.Default);
                if (deserialized != null)
                    attributes = deserialized;
            }
            catch { /* best-effort */ }
        }

        return new AvatarNFT
        {
            Id                  = FromSurrealId(p.Id),
            AvatarId            = FromSurrealId(p.AvatarId),
            ChainType           = p.ChainType,
            NFTContractAddress  = p.ContractAddress,
            TokenId             = p.TokenId,
            TokenStandard       = p.TokenStandard,
            MetadataURI         = p.MetadataUri,
            ImageURI            = p.ImageUri,
            Name                = p.Name,
            Description         = p.Description,
            Attributes          = attributes,
            RoyaltyPercentage   = p.RoyaltyPercentage,
            RoyaltyRecipient    = p.RoyaltyRecipient,
            IsSoulbound         = p.IsSoulbound,
            IsTransferable      = p.IsTransferable,
            CurrentOwner        = p.CurrentOwner,
            IsActive            = p.IsActive,
            MintedDate          = p.MintedDate.UtcDateTime,
            LastTransferDate    = p.LastTransferDate?.UtcDateTime
        };
    }

    // ── HolonNFTBinding mapping ───────────────────────────────────────────────

    private static SurrealHolonBinding ToHolonBindingPoco(IHolonNFTBinding b)
    {
        JsonElement? permissionsJson = null;
        if (b.Permissions.Count > 0)
        {
            var raw = JsonSerializer.Serialize(b.Permissions, SurrealJsonOptions.Default);
            using var doc = JsonDocument.Parse(raw);
            permissionsJson = doc.RootElement.Clone();
        }

        return new SurrealHolonBinding
        {
            Id              = ToSurrealId(b.Id),
            HolonId         = ToSurrealId(b.HolonId),
            AvatarNftId     = ToSurrealId(b.AvatarNFTId),
            Role            = b.Role,
            PermissionLevel = b.PermissionLevel,
            Permissions     = permissionsJson,
            CreatedDate     = new DateTimeOffset(
                                  DateTime.SpecifyKind(b.CreatedDate, DateTimeKind.Utc)),
            LastUpdatedDate = b.LastUpdatedDate.HasValue
                              ? new DateTimeOffset(
                                    DateTime.SpecifyKind(b.LastUpdatedDate.Value, DateTimeKind.Utc))
                              : null,
            IsActive        = b.IsActive
        };
    }

    private static HolonNFTBinding FromSurrealHolonBinding(SurrealHolonBinding s)
    {
        Dictionary<string, string> perms = new();
        if (s.Permissions.HasValue)
        {
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    s.Permissions.Value.GetRawText(), SurrealJsonOptions.Default);
                if (d != null) perms = d;
            }
            catch { /* best-effort */ }
        }

        return new HolonNFTBinding
        {
            Id              = FromSurrealId(s.Id),
            HolonId         = FromSurrealId(s.HolonId),
            AvatarNFTId     = FromSurrealId(s.AvatarNftId),
            Role            = s.Role,
            PermissionLevel = s.PermissionLevel,
            Permissions     = perms,
            CreatedDate     = s.CreatedDate.UtcDateTime,
            LastUpdatedDate = s.LastUpdatedDate?.UtcDateTime,
            IsActive        = s.IsActive
        };
    }

    // ── WalletNFTBinding mapping ──────────────────────────────────────────────

    private static SurrealWalletBinding ToWalletBindingPoco(IWalletNFTBinding b)
    {
        JsonElement? permissionsJson = null;
        if (b.AccessPermissions.Count > 0)
        {
            var raw = JsonSerializer.Serialize(b.AccessPermissions, SurrealJsonOptions.Default);
            using var doc = JsonDocument.Parse(raw);
            permissionsJson = doc.RootElement.Clone();
        }

        return new SurrealWalletBinding
        {
            Id                = ToSurrealId(b.Id),
            WalletId          = ToSurrealId(b.WalletId),
            AvatarNftId       = ToSurrealId(b.AvatarNFTId),
            BindingType       = b.BindingType,
            AccessLevel       = b.AccessLevel,
            AccessPermissions = permissionsJson,
            CreatedDate       = new DateTimeOffset(
                                    DateTime.SpecifyKind(b.CreatedDate, DateTimeKind.Utc)),
            LastUpdatedDate   = b.LastUpdatedDate.HasValue
                                ? new DateTimeOffset(
                                      DateTime.SpecifyKind(b.LastUpdatedDate.Value, DateTimeKind.Utc))
                                : null,
            IsActive          = b.IsActive
        };
    }

    private static WalletNFTBinding FromSurrealWalletBinding(SurrealWalletBinding s)
    {
        Dictionary<string, string> perms = new();
        if (s.AccessPermissions.HasValue)
        {
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    s.AccessPermissions.Value.GetRawText(), SurrealJsonOptions.Default);
                if (d != null) perms = d;
            }
            catch { /* best-effort */ }
        }

        return new WalletNFTBinding
        {
            Id                = FromSurrealId(s.Id),
            WalletId          = FromSurrealId(s.WalletId),
            AvatarNFTId       = FromSurrealId(s.AvatarNftId),
            BindingType       = s.BindingType,
            AccessLevel       = s.AccessLevel,
            AccessPermissions = perms,
            CreatedDate       = s.CreatedDate.UtcDateTime,
            LastUpdatedDate   = s.LastUpdatedDate?.UtcDateTime,
            IsActive          = s.IsActive
        };
    }

    // ── Inline SurrealDB record types for binding tables ──────────────────────
    // These tables have no generated POCOs (not in the Mermaid schema).
    // They are SCHEMALESS (SurrealDB defaults) — adapters manage the shape.

    private sealed class SurrealHolonBinding : Oasis.SurrealDb.Client.ISurrealRecord
    {
        public string SchemaName => HolonBindingTable;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("holon_id")]
        public string HolonId { get; set; } = string.Empty;

        [JsonPropertyName("avatar_nft_id")]
        public string AvatarNftId { get; set; } = string.Empty;

        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("permission_level")]
        public string? PermissionLevel { get; set; }

        [JsonPropertyName("permissions")]
        public JsonElement? Permissions { get; set; }

        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [JsonPropertyName("last_updated_date")]
        public DateTimeOffset? LastUpdatedDate { get; set; }

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;
    }

    private sealed class SurrealWalletBinding : Oasis.SurrealDb.Client.ISurrealRecord
    {
        public string SchemaName => WalletBindingTable;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("wallet_id")]
        public string WalletId { get; set; } = string.Empty;

        [JsonPropertyName("avatar_nft_id")]
        public string AvatarNftId { get; set; } = string.Empty;

        [JsonPropertyName("binding_type")]
        public string BindingType { get; set; } = string.Empty;

        [JsonPropertyName("access_level")]
        public string? AccessLevel { get; set; }

        [JsonPropertyName("access_permissions")]
        public JsonElement? AccessPermissions { get; set; }

        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [JsonPropertyName("last_updated_date")]
        public DateTimeOffset? LastUpdatedDate { get; set; }

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;
    }
}
