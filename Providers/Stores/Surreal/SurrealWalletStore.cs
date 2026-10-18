using SurrealForge.Client;
using SurrealForge.Client.Query;
using SurrealForge.Client.Idempotency;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using GeneratedWallet = AZOA.WebAPI.Persistence.SurrealDb.Models.Wallet;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed partial class SurrealWalletStore(ISurrealExecutor executor) : IWalletStore
{
    /// <inheritdoc/>
    public async Task<AZOAResult<IWallet>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var row = await executor.QuerySingleAsync<GeneratedWallet>(SurrealQuery<GeneratedWallet>.Key(id.ToString("N")), ct);
        return row is null ? AZOAResult<IWallet>.Failure("Wallet not found.") : AZOAResult<IWallet>.Success(FromPoco(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<IWallet>>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await executor.QueryAsync<GeneratedWallet>(SurrealQuery<GeneratedWallet>.From(), ct);
        return AZOAResult<IEnumerable<IWallet>>.Success(rows.Select(FromPoco).Cast<IWallet>().ToArray());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<IWallet>>> GetByUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = SurrealLink.ToLink("user", userId.ToString("N"))!;
        var rows = await executor.QueryAsync<GeneratedWallet>(SurrealQuery<GeneratedWallet>.From().Where(row => row.UserId == user), ct);
        return AZOAResult<IEnumerable<IWallet>>.Success(rows.Select(FromPoco).Cast<IWallet>().ToArray());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IWallet>> UpsertAsync(IWallet wallet, CancellationToken ct = default)
    {
        if (wallet.Id == Guid.Empty) return AZOAResult<IWallet>.Failure("A wallet identity is required.");
        var response = await ExecuteMutationAsync(SurrealWriter.Upsert(ToPoco(wallet)), ct);
        response.EnsureAllOk();
        return AZOAResult<IWallet>.Success(FromPoco(response.GetValues<GeneratedWallet>(2).Single()));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IWallet>> CreateIfAbsentAsync(IWallet wallet, CancellationToken ct = default)
    {
        if (wallet.Id == Guid.Empty) return AZOAResult<IWallet>.Failure("A deterministic wallet identity is required.");
        try
        {
            var response = await ExecuteMutationAsync(SurrealWriter.Create(ToPoco(wallet)), ct);
            response.EnsureAllOk();
            return AZOAResult<IWallet>.Success(FromPoco(response.GetValues<GeneratedWallet>(2).Single()));
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var existing = await GetByIdAsync(wallet.Id, ct);
            if (existing.Result is null) throw;
            return existing;
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await executor.QuerySingleAsync<GeneratedWallet>(SurrealQuery<GeneratedWallet>.Key(id.ToString("N")), ct);
        if (existing is null) return AZOAResult<bool>.Failure("Wallet not found.");
        var owner = existing.UserId;
        var response = await ExecuteMutationAsync(SurrealWriter.DeleteOnly<GeneratedWallet>(id.ToString("N"))
            .Where(row => row.UserId == owner).Build(), ct);
        return response[2].AffectedCount() == 1 ? AZOAResult<bool>.Success(true)
            : AZOAResult<bool>.Failure("Wallet not found.");
    }

    // raw: ordinary wallet writes join the lock used by local action query witnesses.
    private async Task<SurrealResponse> ExecuteMutationAsync(SurrealQuery mutation, CancellationToken ct)
    {
        var transaction = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"), LocalEntityMutationFence.Wallets(),
            SurrealGeneratedStatementScope.Apply(mutation, "wallet_mutation"), SurrealQuery.Of("COMMIT"));
        return await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await executor.ExecuteAsync(transaction, ct);
            response.EnsureAllOk();
            return response;
        }, ct);
    }

    internal static GeneratedWallet ToPoco(IWallet w) => new()
    {
        Id                   = SurrealId.ToSurrealId(w.Id),
        UserId             = SurrealLink.ToLink("user", SurrealId.ToSurrealId(w.UserId))!,
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

    internal static Wallet FromPoco(GeneratedWallet p) => new()
    {
        Id                  = SurrealId.FromSurrealId(p.Id),
        UserId            = SurrealId.FromSurrealId(SurrealLink.FromLink(p.UserId)!),
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
