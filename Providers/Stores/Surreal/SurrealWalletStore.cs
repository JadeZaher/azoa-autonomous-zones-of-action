using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using GeneratedWallet = AZOA.WebAPI.Persistence.SurrealDb.Models.Wallet;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IWalletStore"/>. Maps between the legacy
/// <see cref="Wallet"/> domain model and the generated
/// <see cref="GeneratedWallet"/> POCO via private ToPoco / FromPoco helpers.
///
/// ID encoding: <c>Guid.ToString("N").ToLowerInvariant()</c> (32-char hex,
/// no dashes). Dates are stored as DateTimeOffset with UTC kind applied.
/// </summary>
public sealed class SurrealWalletStore : IWalletStore
{
    private readonly ISurrealExecutor _executor;

    public SurrealWalletStore(ISurrealExecutor executor)
    {
        _executor = executor;
    }

    // ── IWalletStore ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<IWallet>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectById(GeneratedWallet.SchemaNameConst, SurrealId.ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<GeneratedWallet>(q, ct);
            return new AZOAResult<IWallet>
            {
                IsError = row == null,
                Message = row == null ? "Wallet not found." : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IWallet>().CaptureException(ex, $"SurrealWalletStore.GetByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<IWallet>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectAll(GeneratedWallet.SchemaNameConst);
            var rows = await _executor.QueryAsync<GeneratedWallet>(q, ct);
            return new AZOAResult<IEnumerable<IWallet>>
            {
                Result  = rows.Select(FromPoco).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<IWallet>>().CaptureException(ex, $"SurrealWalletStore.GetAllAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<IWallet>>> GetByAvatarAsync(Guid avatarId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery<GeneratedWallet>.From()
                .Where(w => w.AvatarId == SurrealLink.ToLink("avatar", avatarId.ToString("N").ToLowerInvariant()));
            var rows = await _executor.QueryAsync<GeneratedWallet>(q, ct);
            return new AZOAResult<IEnumerable<IWallet>>
            {
                Result  = rows.Select(FromPoco).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<IWallet>>().CaptureException(ex, $"SurrealWalletStore.GetByAvatarAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IWallet>> UpsertAsync(IWallet wallet, CancellationToken ct = default)
    {
        try
        {
            if (wallet.Id == Guid.Empty)
                wallet.Id = Guid.NewGuid();

            var poco = ToPoco(wallet);

            var q    = SurrealWriter.Upsert(poco);
            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved = resp.GetValues<GeneratedWallet>(0).FirstOrDefault();
            var result = saved is not null ? FromPoco(saved) : wallet;

            return new AZOAResult<IWallet> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IWallet>().CaptureException(ex, $"SurrealWalletStore.UpsertAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            // Check existence first (matches the prior EF read-before-update contract).
            var checkQ = SurrealQuery.SelectById(GeneratedWallet.SchemaNameConst, SurrealId.ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<GeneratedWallet>(checkQ, ct);
            if (existing == null)
                return new AZOAResult<bool> { IsError = true, Message = "Wallet not found.", Result = false };

            var q = SurrealQuery.DeleteById(GeneratedWallet.SchemaNameConst, SurrealId.ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new AZOAResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealWalletStore.DeleteAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────



    private static GeneratedWallet ToPoco(IWallet w) => new()
    {
        Id                   = SurrealId.ToSurrealId(w.Id),
        AvatarId             = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(w.AvatarId)),
        ChainType            = w.ChainType,
        Address              = w.Address,
        PublicKey            = w.PublicKey,
        Label                = w.Label,
        IsDefault            = w.IsDefault,
        WalletType           = MapWalletType(w.WalletType),
        EncryptedPrivateKey  = w.EncryptedPrivateKey,
        EncryptedSeedPhrase  = w.EncryptedSeedPhrase,
        CreatedDate          = new DateTimeOffset(
                                   DateTime.SpecifyKind(w.CreatedDate, DateTimeKind.Utc))
    };

    private static Wallet FromPoco(GeneratedWallet p) => new()
    {
        Id                  = SurrealId.FromSurrealId(p.Id),
        AvatarId            = SurrealId.FromSurrealId(SurrealLink.FromLink(p.AvatarId)!),
        ChainType           = p.ChainType,
        Address             = p.Address,
        PublicKey           = p.PublicKey,
        Label               = p.Label,
        IsDefault           = p.IsDefault,
        WalletType          = MapWalletTypeBack(p.WalletType),
        EncryptedPrivateKey = p.EncryptedPrivateKey,
        EncryptedSeedPhrase = p.EncryptedSeedPhrase,
        CreatedDate         = p.CreatedDate.UtcDateTime
    };

    private static GeneratedWallet.WalletTypeKind MapWalletType(WalletType t) => t switch
    {
        WalletType.Platform => GeneratedWallet.WalletTypeKind.Platform,
        _                   => GeneratedWallet.WalletTypeKind.External
    };

    private static WalletType MapWalletTypeBack(GeneratedWallet.WalletTypeKind k) => k switch
    {
        GeneratedWallet.WalletTypeKind.Platform => WalletType.Platform,
        _                                       => WalletType.External
    };

}
