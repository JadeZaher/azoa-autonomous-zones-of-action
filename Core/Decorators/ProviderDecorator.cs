using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Core.Decorators;

/// <summary>
/// Base decorator for IOASISStorageProvider. Apply cross-cutting concerns
/// (logging, metrics, retry, health tracking) without modifying provider implementations.
/// </summary>
public abstract class ProviderDecorator : IOASISStorageProvider
{
    protected readonly IOASISStorageProvider Inner;

    public virtual string ProviderName => Inner.ProviderName;

    protected ProviderDecorator(IOASISStorageProvider inner)
    {
        Inner = inner;
    }

    public virtual Task<OASISResult<IAvatar>> LoadAvatarAsync(Guid id, CancellationToken ct = default)
        => Inner.LoadAvatarAsync(id, ct);

    public virtual Task<OASISResult<IAvatar>> SaveAvatarAsync(IAvatar avatar, CancellationToken ct = default)
        => Inner.SaveAvatarAsync(avatar, ct);

    public virtual Task<OASISResult<bool>> DeleteAvatarAsync(Guid id, CancellationToken ct = default)
        => Inner.DeleteAvatarAsync(id, ct);

    public virtual Task<OASISResult<IEnumerable<IAvatar>>> LoadAllAvatarsAsync(CancellationToken ct = default)
        => Inner.LoadAllAvatarsAsync(ct);

    public virtual Task<OASISResult<IWallet>> LoadWalletAsync(Guid id, CancellationToken ct = default)
        => Inner.LoadWalletAsync(id, ct);

    public virtual Task<OASISResult<IWallet>> SaveWalletAsync(IWallet wallet, CancellationToken ct = default)
        => Inner.SaveWalletAsync(wallet, ct);

    public virtual Task<OASISResult<bool>> DeleteWalletAsync(Guid id, CancellationToken ct = default)
        => Inner.DeleteWalletAsync(id, ct);

    public virtual Task<OASISResult<IEnumerable<IWallet>>> LoadWalletsByAvatarAsync(Guid avatarId, CancellationToken ct = default)
        => Inner.LoadWalletsByAvatarAsync(avatarId, ct);

    public virtual Task<OASISResult<IEnumerable<IWallet>>> LoadAllWalletsAsync(CancellationToken ct = default)
        => Inner.LoadAllWalletsAsync(ct);

    public virtual Task<OASISResult<IHolon>> LoadHolonAsync(Guid id, CancellationToken ct = default)
        => Inner.LoadHolonAsync(id, ct);

    public virtual Task<OASISResult<IHolon>> SaveHolonAsync(IHolon holon, CancellationToken ct = default)
        => Inner.SaveHolonAsync(holon, ct);

    public virtual Task<OASISResult<bool>> DeleteHolonAsync(Guid id, CancellationToken ct = default)
        => Inner.DeleteHolonAsync(id, ct);

    public virtual Task<OASISResult<IEnumerable<IHolon>>> LoadAllHolonsAsync(HolonQueryRequest? query = null, CancellationToken ct = default)
        => Inner.LoadAllHolonsAsync(query, ct);

    public virtual Task<OASISResult<IBlockchainOperation>> LoadBlockchainOperationAsync(Guid id, CancellationToken ct = default)
        => Inner.LoadBlockchainOperationAsync(id, ct);

    public virtual Task<OASISResult<IBlockchainOperation>> SaveBlockchainOperationAsync(IBlockchainOperation operation, CancellationToken ct = default)
        => Inner.SaveBlockchainOperationAsync(operation, ct);

    public virtual Task<OASISResult<bool>> DeleteBlockchainOperationAsync(Guid id, CancellationToken ct = default)
        => Inner.DeleteBlockchainOperationAsync(id, ct);

    public virtual Task<OASISResult<IEnumerable<IBlockchainOperation>>> LoadBlockchainOperationsByAvatarAsync(Guid avatarId, CancellationToken ct = default)
        => Inner.LoadBlockchainOperationsByAvatarAsync(avatarId, ct);

    public virtual Task<OASISResult<ISTARODK>> LoadSTARODKAsync(Guid id, CancellationToken ct = default)
        => Inner.LoadSTARODKAsync(id, ct);

    public virtual Task<OASISResult<ISTARODK>> SaveSTARODKAsync(ISTARODK odk, CancellationToken ct = default)
        => Inner.SaveSTARODKAsync(odk, ct);

    public virtual Task<OASISResult<bool>> DeleteSTARODKAsync(Guid id, CancellationToken ct = default)
        => Inner.DeleteSTARODKAsync(id, ct);

    public virtual Task<OASISResult<IEnumerable<ISTARODK>>> LoadAllSTARODKsAsync(CancellationToken ct = default)
        => Inner.LoadAllSTARODKsAsync(ct);
}
