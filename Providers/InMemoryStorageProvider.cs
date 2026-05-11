using System.Collections.Concurrent;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Providers;

public class InMemoryStorageProvider : IOASISStorageProvider
{
    private readonly ConcurrentDictionary<Guid, IAvatar> _avatars = new();
    private readonly ConcurrentDictionary<Guid, IWallet> _wallets = new();
    private readonly ConcurrentDictionary<Guid, IHolon> _holons = new();
    private readonly ConcurrentDictionary<Guid, IBlockchainOperation> _operations = new();
    private readonly ConcurrentDictionary<Guid, ISTARODK> _stars = new();
    private readonly ConcurrentDictionary<Guid, Quest> _quests = new();
    private readonly ConcurrentDictionary<Guid, QuestTemplate> _questTemplates = new();
    private readonly ConcurrentDictionary<Guid, QuestNodeTemplate> _questNodeTemplates = new();

    public string ProviderName => "InMemory";

    // Avatar
    public Task<OASISResult<IAvatar>> LoadAvatarAsync(Guid id, CancellationToken ct = default)
    {
        _avatars.TryGetValue(id, out var avatar);
        return Task.FromResult(new OASISResult<IAvatar>
        {
            IsError = avatar == null,
            Message = avatar == null ? "Avatar not found." : "Success",
            Result = avatar
        });
    }

    public Task<OASISResult<IAvatar>> SaveAvatarAsync(IAvatar avatar, CancellationToken ct = default)
    {
        _avatars[avatar.Id] = avatar;
        return Task.FromResult(new OASISResult<IAvatar> { Result = avatar, Message = "Saved." });
    }

    public Task<OASISResult<bool>> DeleteAvatarAsync(Guid id, CancellationToken ct = default)
    {
        var ok = _avatars.TryRemove(id, out _);
        return Task.FromResult(new OASISResult<bool> { Result = ok, Message = ok ? "Deleted." : "Not found." });
    }

    public Task<OASISResult<IEnumerable<IAvatar>>> LoadAllAvatarsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new OASISResult<IEnumerable<IAvatar>>
        {
            Result = _avatars.Values.ToList(),
            Message = "Success"
        });
    }

    // Wallet
    public Task<OASISResult<IWallet>> LoadWalletAsync(Guid id, CancellationToken ct = default)
    {
        _wallets.TryGetValue(id, out var wallet);
        return Task.FromResult(new OASISResult<IWallet>
        {
            IsError = wallet == null,
            Message = wallet == null ? "Wallet not found." : "Success",
            Result = wallet
        });
    }

    public Task<OASISResult<IWallet>> SaveWalletAsync(IWallet wallet, CancellationToken ct = default)
    {
        _wallets[wallet.Id] = wallet;
        return Task.FromResult(new OASISResult<IWallet> { Result = wallet, Message = "Saved." });
    }

    public Task<OASISResult<bool>> DeleteWalletAsync(Guid id, CancellationToken ct = default)
    {
        var ok = _wallets.TryRemove(id, out _);
        return Task.FromResult(new OASISResult<bool> { Result = ok, Message = ok ? "Deleted." : "Not found." });
    }

    public Task<OASISResult<IEnumerable<IWallet>>> LoadWalletsByAvatarAsync(Guid avatarId, CancellationToken ct = default)
    {
        var list = _wallets.Values.Where(w => w.AvatarId == avatarId).ToList();
        return Task.FromResult(new OASISResult<IEnumerable<IWallet>> { Result = list, Message = "Success" });
    }

    public Task<OASISResult<IEnumerable<IWallet>>> LoadAllWalletsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new OASISResult<IEnumerable<IWallet>> { Result = _wallets.Values.ToList(), Message = "Success" });
    }

    // Holon
    public Task<OASISResult<IHolon>> LoadHolonAsync(Guid id, CancellationToken ct = default)
    {
        _holons.TryGetValue(id, out var holon);
        return Task.FromResult(new OASISResult<IHolon>
        {
            IsError = holon == null,
            Message = holon == null ? "Holon not found." : "Success",
            Result = holon
        });
    }

    public Task<OASISResult<IHolon>> SaveHolonAsync(IHolon holon, CancellationToken ct = default)
    {
        _holons[holon.Id] = holon;
        return Task.FromResult(new OASISResult<IHolon> { Result = holon, Message = "Saved." });
    }

    public Task<OASISResult<bool>> DeleteHolonAsync(Guid id, CancellationToken ct = default)
    {
        var ok = _holons.TryRemove(id, out _);
        return Task.FromResult(new OASISResult<bool> { Result = ok, Message = ok ? "Deleted." : "Not found." });
    }

    public Task<OASISResult<IEnumerable<IHolon>>> LoadAllHolonsAsync(HolonQueryRequest? query = null, CancellationToken ct = default)
    {
        var all = _holons.Values.AsEnumerable();

        if (query != null)
        {
            if (!string.IsNullOrEmpty(query.Name))
                all = all.Where(h => h.Name.Contains(query.Name, StringComparison.OrdinalIgnoreCase));
            if (query.AvatarId.HasValue)
                all = all.Where(h => h.AvatarId == query.AvatarId);
            if (!string.IsNullOrEmpty(query.ProviderName))
                all = all.Where(h => h.ProviderName.Equals(query.ProviderName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(query.ChainId))
                all = all.Where(h => h.ChainId == query.ChainId);
            if (!string.IsNullOrEmpty(query.AssetType))
                all = all.Where(h => h.AssetType == query.AssetType);
            if (query.IsActive.HasValue)
                all = all.Where(h => h.IsActive == query.IsActive.Value);
            if (query.ParentHolonId.HasValue)
                all = all.Where(h => h.ParentHolonId == query.ParentHolonId.Value);
        }

        return Task.FromResult(new OASISResult<IEnumerable<IHolon>> { Result = all.ToList(), Message = "Success" });
    }

    // Blockchain Operation
    public Task<OASISResult<IBlockchainOperation>> LoadBlockchainOperationAsync(Guid id, CancellationToken ct = default)
    {
        _operations.TryGetValue(id, out var op);
        return Task.FromResult(new OASISResult<IBlockchainOperation>
        {
            IsError = op == null,
            Message = op == null ? "Operation not found." : "Success",
            Result = op
        });
    }

    public Task<OASISResult<IBlockchainOperation>> SaveBlockchainOperationAsync(IBlockchainOperation operation, CancellationToken ct = default)
    {
        _operations[operation.Id] = operation;
        return Task.FromResult(new OASISResult<IBlockchainOperation> { Result = operation, Message = "Saved." });
    }

    public Task<OASISResult<bool>> DeleteBlockchainOperationAsync(Guid id, CancellationToken ct = default)
    {
        var ok = _operations.TryRemove(id, out _);
        return Task.FromResult(new OASISResult<bool> { Result = ok, Message = ok ? "Deleted." : "Not found." });
    }

    public Task<OASISResult<IEnumerable<IBlockchainOperation>>> LoadBlockchainOperationsByAvatarAsync(Guid avatarId, CancellationToken ct = default)
    {
        var list = _operations.Values.Where(o => o.AvatarId == avatarId).ToList();
        return Task.FromResult(new OASISResult<IEnumerable<IBlockchainOperation>> { Result = list, Message = "Success" });
    }

    // STAR ODK
    public Task<OASISResult<ISTARODK>> LoadSTARODKAsync(Guid id, CancellationToken ct = default)
    {
        _stars.TryGetValue(id, out var star);
        return Task.FromResult(new OASISResult<ISTARODK>
        {
            IsError = star == null,
            Message = star == null ? "STAR ODK not found." : "Success",
            Result = star
        });
    }

    public Task<OASISResult<ISTARODK>> SaveSTARODKAsync(ISTARODK odk, CancellationToken ct = default)
    {
        _stars[odk.Id] = odk;
        return Task.FromResult(new OASISResult<ISTARODK> { Result = odk, Message = "Saved." });
    }

    public Task<OASISResult<bool>> DeleteSTARODKAsync(Guid id, CancellationToken ct = default)
    {
        var ok = _stars.TryRemove(id, out _);
        return Task.FromResult(new OASISResult<bool> { Result = ok, Message = ok ? "Deleted." : "Not found." });
    }

    public Task<OASISResult<IEnumerable<ISTARODK>>> LoadAllSTARODKsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new OASISResult<IEnumerable<ISTARODK>>
        {
            Result = _stars.Values.ToList(),
            Message = "Success"
        });
    }

    // Quest
    public Task<OASISResult<Quest>> SaveQuestAsync(Quest quest, CancellationToken ct = default)
    {
        _quests[quest.Id] = quest;
        return Task.FromResult(new OASISResult<Quest> { Result = quest, Message = "Saved." });
    }

    public Task<OASISResult<Quest>> LoadQuestAsync(Guid id, CancellationToken ct = default)
    {
        _quests.TryGetValue(id, out var quest);
        return Task.FromResult(new OASISResult<Quest>
        {
            IsError = quest == null,
            Message = quest == null ? "Quest not found." : "Success",
            Result = quest
        });
    }

    public Task<OASISResult<IEnumerable<Quest>>> LoadQuestsByAvatarAsync(Guid avatarId, CancellationToken ct = default)
    {
        var list = _quests.Values.Where(q => q.AvatarId == avatarId).ToList();
        return Task.FromResult(new OASISResult<IEnumerable<Quest>> { Result = list, Message = "Success" });
    }

    public Task<OASISResult<bool>> DeleteQuestAsync(Guid id, CancellationToken ct = default)
    {
        var ok = _quests.TryRemove(id, out _);
        return Task.FromResult(new OASISResult<bool> { Result = ok, Message = ok ? "Deleted." : "Not found." });
    }

    // Quest Template
    public Task<OASISResult<QuestTemplate>> SaveQuestTemplateAsync(QuestTemplate template, CancellationToken ct = default)
    {
        _questTemplates[template.Id] = template;
        return Task.FromResult(new OASISResult<QuestTemplate> { Result = template, Message = "Saved." });
    }

    public Task<OASISResult<QuestTemplate>> LoadQuestTemplateAsync(Guid id, CancellationToken ct = default)
    {
        _questTemplates.TryGetValue(id, out var template);
        return Task.FromResult(new OASISResult<QuestTemplate>
        {
            IsError = template == null,
            Message = template == null ? "Quest template not found." : "Success",
            Result = template
        });
    }

    public Task<OASISResult<IEnumerable<QuestTemplate>>> LoadAllQuestTemplatesAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new OASISResult<IEnumerable<QuestTemplate>>
        {
            Result = _questTemplates.Values.ToList(),
            Message = "Success"
        });
    }

    public Task<OASISResult<bool>> DeleteQuestTemplateAsync(Guid id, CancellationToken ct = default)
    {
        var ok = _questTemplates.TryRemove(id, out _);
        return Task.FromResult(new OASISResult<bool> { Result = ok, Message = ok ? "Deleted." : "Not found." });
    }

    // Quest Node Template
    public Task<OASISResult<QuestNodeTemplate>> SaveQuestNodeTemplateAsync(QuestNodeTemplate template, CancellationToken ct = default)
    {
        _questNodeTemplates[template.Id] = template;
        return Task.FromResult(new OASISResult<QuestNodeTemplate> { Result = template, Message = "Saved." });
    }

    public Task<OASISResult<IEnumerable<QuestNodeTemplate>>> LoadAllQuestNodeTemplatesAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new OASISResult<IEnumerable<QuestNodeTemplate>>
        {
            Result = _questNodeTemplates.Values.ToList(),
            Message = "Success"
        });
    }

    // NFT Extension stubs
public Task<OASISResult<IAvatarNFT>> SaveAvatarNFTAsync(IAvatarNFT n, CancellationToken ct = default) => Task.FromResult(new OASISResult<IAvatarNFT> { Result = n, Message = "Saved" });
public Task<OASISResult<IAvatarNFT>> LoadAvatarNFTAsync(Guid id, CancellationToken ct = default) => Task.FromResult(new OASISResult<IAvatarNFT> { IsError = true, Message = "Not implemented" });
public Task<OASISResult<IAvatarNFT>> LoadAvatarNFTByTokenIdAsync(string c, string n, string t, CancellationToken ct = default) => Task.FromResult(new OASISResult<IAvatarNFT> { IsError = true, Message = "Not implemented" });
public Task<OASISResult<IEnumerable<IAvatarNFT>>> LoadAvatarNFTsByAvatarAsync(Guid a, CancellationToken ct = default) => Task.FromResult<OASISResult<IEnumerable<IAvatarNFT>>>(new() { Result = Array.Empty<IAvatarNFT>(), Message = "Success" });
public Task<OASISResult<bool>> DeleteAvatarNFTAsync(Guid id, CancellationToken ct = default) => Task.FromResult(new OASISResult<bool> { IsError = true, Message = "Not implemented" });
public Task<OASISResult<IHolonNFTBinding>> SaveHolonNFTBindingAsync(IHolonNFTBinding b, CancellationToken ct = default) => Task.FromResult(new OASISResult<IHolonNFTBinding> { Result = b, Message = "Saved" });
public Task<OASISResult<IHolonNFTBinding>> LoadHolonNFTBindingAsync(Guid id, CancellationToken ct = default) => Task.FromResult(new OASISResult<IHolonNFTBinding> { IsError = true, Message = "Not implemented" });
public Task<OASISResult<IEnumerable<IHolonNFTBinding>>> LoadHolonNFTBindingsByAvatarNFTAsync(Guid a, CancellationToken ct = default) => Task.FromResult<OASISResult<IEnumerable<IHolonNFTBinding>>>(new() { Result = Array.Empty<IHolonNFTBinding>(), Message = "Success" });
public Task<OASISResult<bool>> DeleteHolonNFTBindingAsync(Guid id, CancellationToken ct = default) => Task.FromResult(new OASISResult<bool> { IsError = true, Message = "Not implemented" });
public Task<OASISResult<IWalletNFTBinding>> SaveWalletNFTBindingAsync(IWalletNFTBinding b, CancellationToken ct = default) => Task.FromResult(new OASISResult<IWalletNFTBinding> { Result = b, Message = "Saved" });
public Task<OASISResult<IWalletNFTBinding>> LoadWalletNFTBindingAsync(Guid id, CancellationToken ct = default) => Task.FromResult(new OASISResult<IWalletNFTBinding> { IsError = true, Message = "Not implemented" });
public Task<OASISResult<IEnumerable<IWalletNFTBinding>>> LoadWalletNFTBindingsByAvatarNFTAsync(Guid a, CancellationToken ct = default) => Task.FromResult<OASISResult<IEnumerable<IWalletNFTBinding>>>(new() { Result = Array.Empty<IWalletNFTBinding>(), Message = "Success" });
public Task<OASISResult<bool>> DeleteWalletNFTBindingAsync(Guid id, CancellationToken ct = default) => Task.FromResult(new OASISResult<bool> { IsError = true, Message = "Not implemented" });
}
