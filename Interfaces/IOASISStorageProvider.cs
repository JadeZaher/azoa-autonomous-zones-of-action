using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces;

public interface IOASISStorageProvider
{
    string ProviderName { get; }

    // Avatar
    Task<OASISResult<IAvatar>> LoadAvatarAsync(Guid id, CancellationToken ct = default);
    Task<OASISResult<IAvatar>> SaveAvatarAsync(IAvatar avatar, CancellationToken ct = default);
    Task<OASISResult<bool>> DeleteAvatarAsync(Guid id, CancellationToken ct = default);
    Task<OASISResult<IEnumerable<IAvatar>>> LoadAllAvatarsAsync(CancellationToken ct = default);

    // Wallet
    Task<OASISResult<IWallet>> LoadWalletAsync(Guid id, CancellationToken ct = default);
    Task<OASISResult<IWallet>> SaveWalletAsync(IWallet wallet, CancellationToken ct = default);
    Task<OASISResult<bool>> DeleteWalletAsync(Guid id, CancellationToken ct = default);
    Task<OASISResult<IEnumerable<IWallet>>> LoadWalletsByAvatarAsync(Guid avatarId, CancellationToken ct = default);
    Task<OASISResult<IEnumerable<IWallet>>> LoadAllWalletsAsync(CancellationToken ct = default);

    // Holon
    Task<OASISResult<IHolon>> LoadHolonAsync(Guid id, CancellationToken ct = default);
    Task<OASISResult<IHolon>> SaveHolonAsync(IHolon holon, CancellationToken ct = default);
    Task<OASISResult<bool>> DeleteHolonAsync(Guid id, CancellationToken ct = default);
    Task<OASISResult<IEnumerable<IHolon>>> LoadAllHolonsAsync(HolonQueryRequest? query = null, CancellationToken ct = default);

    // Blockchain Operation
    Task<OASISResult<IBlockchainOperation>> LoadBlockchainOperationAsync(Guid id, CancellationToken ct = default);
    Task<OASISResult<IBlockchainOperation>> SaveBlockchainOperationAsync(IBlockchainOperation operation, CancellationToken ct = default);
    Task<OASISResult<bool>> DeleteBlockchainOperationAsync(Guid id, CancellationToken ct = default);
    Task<OASISResult<IEnumerable<IBlockchainOperation>>> LoadBlockchainOperationsByAvatarAsync(Guid avatarId, CancellationToken ct = default);

    // STAR ODK
    Task<OASISResult<ISTARODK>> LoadSTARODKAsync(Guid id, CancellationToken ct = default);
    Task<OASISResult<ISTARODK>> SaveSTARODKAsync(ISTARODK odk, CancellationToken ct = default);
    Task<OASISResult<bool>> DeleteSTARODKAsync(Guid id, CancellationToken ct = default);
    Task<OASISResult<IEnumerable<ISTARODK>>> LoadAllSTARODKsAsync(CancellationToken ct = default);
}
