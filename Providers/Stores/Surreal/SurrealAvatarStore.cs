using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IAvatarStore"/>. Maps between the legacy
/// <see cref="Avatar"/> domain model and an inline POCO (no source-gen this round)
/// via private ToPoco / FromPoco helpers.
///
/// ID encoding: <c>Guid.ToString("N").ToLowerInvariant()</c> (32-char hex,
/// no dashes). Dates are stored as DateTimeOffset with UTC kind applied.
/// </summary>
public sealed class SurrealAvatarStore : IAvatarStore
{
    private const string AvatarTable = "avatar";

    private readonly ISurrealExecutor _executor;

    public SurrealAvatarStore(ISurrealExecutor executor)
    {
        _executor = executor;
    }

    // ── IAvatarStore ──────────────────────────────────────────────────────────

    public async Task<OASISResult<IAvatar>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectById(AvatarTable, ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<SurrealAvatar>(q, ct);
            return new OASISResult<IAvatar>
            {
                IsError = row == null,
                Message = row == null ? "Avatar not found." : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IAvatar>().CaptureException(ex, $"SurrealAvatarStore.GetByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<IEnumerable<IAvatar>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectAll(AvatarTable);
            var rows = await _executor.QueryAsync<SurrealAvatar>(q, ct);
            return new OASISResult<IEnumerable<IAvatar>>
            {
                Result  = rows.Select(FromPoco).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IEnumerable<IAvatar>>().CaptureException(ex, $"SurrealAvatarStore.GetAllAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<IAvatar>> UpsertAsync(IAvatar avatar, CancellationToken ct = default)
    {
        try
        {
            if (avatar.Id == Guid.Empty)
                avatar.Id = Guid.NewGuid();

            var poco   = ToPoco(avatar);
            var surrId = poco.Id;

            // UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER
            // SurrealDB upsert: creates the record if it does not exist; replaces
            // it if it does. Same pattern as SurrealWalletStore.
            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    AvatarTable)
                .WithParam("_id",   surrId)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved = resp.GetValues<SurrealAvatar>(0).FirstOrDefault();
            var result = saved is not null ? FromPoco(saved) : avatar;

            return new OASISResult<IAvatar> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new OASISResult<IAvatar>().CaptureException(ex, $"SurrealAvatarStore.UpsertAsync failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            // Check existence first (matches the prior EF read-before-update contract).
            var checkQ = SurrealQuery.SelectById(AvatarTable, ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<SurrealAvatar>(checkQ, ct);
            if (existing == null)
                return new OASISResult<bool> { IsError = true, Message = "Avatar not found.", Result = false };

            var q = SurrealQuery.DeleteById(AvatarTable, ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new OASISResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new OASISResult<bool>().CaptureException(ex, $"SurrealAvatarStore.DeleteAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static string ToSurrealId(Guid id)
        => id.ToString("N").ToLowerInvariant();

    private static Guid FromSurrealId(string id)
        => Guid.ParseExact(id, "N");

    private static SurrealAvatar ToPoco(IAvatar a) => new()
    {
        Id               = ToSurrealId(a.Id),
        Username         = a.Username,
        Email            = a.Email,
        PasswordHash     = a.PasswordHash,
        Title            = a.Title,
        FirstName        = a.FirstName,
        LastName         = a.LastName,
        CreatedDate      = new DateTimeOffset(
                               DateTime.SpecifyKind(a.CreatedDate, DateTimeKind.Utc)),
        LastBeamedInDate = a.LastBeamedInDate.HasValue
                               ? new DateTimeOffset(
                                     DateTime.SpecifyKind(a.LastBeamedInDate.Value, DateTimeKind.Utc))
                               : null,
        IsActive         = a.IsActive,
        IsVerified       = a.IsVerified,
        Karma            = a.Karma,
        Level            = a.Level
    };

    private static Avatar FromPoco(SurrealAvatar p) => new()
    {
        Id               = FromSurrealId(p.Id),
        Username         = p.Username,
        Email            = p.Email,
        PasswordHash     = p.PasswordHash,
        Title            = p.Title,
        FirstName        = p.FirstName,
        LastName         = p.LastName,
        CreatedDate      = p.CreatedDate.UtcDateTime,
        LastBeamedInDate = p.LastBeamedInDate?.UtcDateTime,
        IsActive         = p.IsActive,
        IsVerified       = p.IsVerified,
        Karma            = p.Karma,
        Level            = p.Level
    };

    // ── Inline POCO ───────────────────────────────────────────────────────────

    // TODO: replace with generated POCO when source-gen catches up to wave-2 aggregates.
    private sealed class SurrealAvatar : Oasis.SurrealDb.Client.ISurrealRecord
    {
        public string SchemaName => AvatarTable;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("password_hash")]
        public string PasswordHash { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("first_name")]
        public string? FirstName { get; set; }

        [JsonPropertyName("last_name")]
        public string? LastName { get; set; }

        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [JsonPropertyName("last_beamed_in_date")]
        public DateTimeOffset? LastBeamedInDate { get; set; }

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;

        [JsonPropertyName("is_verified")]
        public bool IsVerified { get; set; }

        [JsonPropertyName("karma")]
        public int Karma { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; } = 1;
    }
}
