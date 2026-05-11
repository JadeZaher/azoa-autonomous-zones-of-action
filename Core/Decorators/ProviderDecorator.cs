using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Quest;
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

    // Quest methods (delegate to inner)
    public virtual Task<OASISResult<Quest>> SaveQuestAsync(Quest quest, CancellationToken ct = default) => Inner.SaveQuestAsync(quest, ct);
    public virtual Task<OASISResult<Quest>> LoadQuestAsync(Guid id, CancellationToken ct = default) => Inner.LoadQuestAsync(id, ct);
    public virtual Task<OASISResult<IEnumerable<Quest>>> LoadQuestsByAvatarAsync(Guid avatarId, CancellationToken ct = default) => Inner.LoadQuestsByAvatarAsync(avatarId, ct);
    public virtual Task<OASISResult<bool>> DeleteQuestAsync(Guid id, CancellationToken ct = default) => Inner.DeleteQuestAsync(id, ct);
    public virtual Task<OASISResult<QuestTemplate>> SaveQuestTemplateAsync(QuestTemplate template, CancellationToken ct = default) => Inner.SaveQuestTemplateAsync(template, ct);
    public virtual Task<OASISResult<QuestTemplate>> LoadQuestTemplateAsync(Guid id, CancellationToken ct = default) => Inner.LoadQuestTemplateAsync(id, ct);
    public virtual Task<OASISResult<IEnumerable<QuestTemplate>>> LoadAllQuestTemplatesAsync(CancellationToken ct = default) => Inner.LoadAllQuestTemplatesAsync(ct);
    public virtual Task<OASISResult<bool>> DeleteQuestTemplateAsync(Guid id, CancellationToken ct = default) => Inner.DeleteQuestTemplateAsync(id, ct);
    public virtual Task<OASISResult<QuestNodeTemplate>> SaveQuestNodeTemplateAsync(QuestNodeTemplate template, CancellationToken ct = default) => Inner.SaveQuestNodeTemplateAsync(template, ct);
    public virtual Task<OASISResult<IEnumerable<QuestNodeTemplate>>> LoadAllQuestNodeTemplatesAsync(CancellationToken ct = default) => Inner.LoadAllQuestNodeTemplatesAsync(ct);

    // NFT Extension methods (delegate to inner)
    public virtual Task<OASISResult<IAvatarNFT>> SaveAvatarNFTAsync(IAvatarNFT n, CancellationToken ct = default) => Inner.SaveAvatarNFTAsync(n, ct);
    public virtual Task<OASISResult<IAvatarNFT>> LoadAvatarNFTAsync(Guid id, CancellationToken ct = default) => Inner.LoadAvatarNFTAsync(id, ct);
    public virtual Task<OASISResult<IAvatarNFT>> LoadAvatarNFTByTokenIdAsync(string c, string n, string t, CancellationToken ct = default) => Inner.LoadAvatarNFTByTokenIdAsync(c, n, t, ct);
    public virtual Task<OASISResult<IEnumerable<IAvatarNFT>>> LoadAvatarNFTsByAvatarAsync(Guid a, CancellationToken ct = default) => Inner.LoadAvatarNFTsByAvatarAsync(a, ct);
    public virtual Task<OASISResult<bool>> DeleteAvatarNFTAsync(Guid id, CancellationToken ct = default) => Inner.DeleteAvatarNFTAsync(id, ct);
    public virtual Task<OASISResult<IHolonNFTBinding>> SaveHolonNFTBindingAsync(IHolonNFTBinding b, CancellationToken ct = default) => Inner.SaveHolonNFTBindingAsync(b, ct);
    public virtual Task<OASISResult<IHolonNFTBinding>> LoadHolonNFTBindingAsync(Guid id, CancellationToken ct = default) => Inner.LoadHolonNFTBindingAsync(id, ct);
    public virtual Task<OASISResult<IEnumerable<IHolonNFTBinding>>> LoadHolonNFTBindingsByAvatarNFTAsync(Guid a, CancellationToken ct = default) => Inner.LoadHolonNFTBindingsByAvatarNFTAsync(a, ct);
    public virtual Task<OASISResult<bool>> DeleteHolonNFTBindingAsync(Guid id, CancellationToken ct = default) => Inner.DeleteHolonNFTBindingAsync(id, ct);
    public virtual Task<OASISResult<IWalletNFTBinding>> SaveWalletNFTBindingAsync(IWalletNFTBinding b, CancellationToken ct = default) => Inner.SaveWalletNFTBindingAsync(b, ct);
    public virtual Task<OASISResult<IWalletNFTBinding>> LoadWalletNFTBindingAsync(Guid id, CancellationToken ct = default) => Inner.LoadWalletNFTBindingAsync(id, ct);
    public virtual Task<OASISResult<IEnumerable<IWalletNFTBinding>>> LoadWalletNFTBindingsByAvatarNFTAsync(Guid a, CancellationToken ct = default) => Inner.LoadWalletNFTBindingsByAvatarNFTAsync(a, ct);
    public virtual Task<OASISResult<bool>> DeleteWalletNFTBindingAsync(Guid id, CancellationToken ct = default) => Inner.DeleteWalletNFTBindingAsync(id, ct);
}
