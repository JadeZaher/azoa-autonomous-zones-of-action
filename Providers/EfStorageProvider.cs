using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Providers;

public class EfStorageProvider : IOASISStorageProvider, IOASISStorageProviderNFTExtensions
{
    private readonly OASISDbContext _db;

    public EfStorageProvider(OASISDbContext db)
    {
        _db = db;
    }

    public string ProviderName => "PostgreSQL";

    // Avatar
    public async Task<OASISResult<IAvatar>> LoadAvatarAsync(Guid id, CancellationToken ct = default)
    {
        var avatar = await _db.Avatars.FindAsync(new object[] { id }, ct);
        return new OASISResult<IAvatar>
        {
            IsError = avatar == null,
            Message = avatar == null ? "Avatar not found." : "Success",
            Result = avatar
        };
    }

    public async Task<OASISResult<IAvatar>> SaveAvatarAsync(IAvatar avatar, CancellationToken ct = default)
    {
        var existing = await _db.Avatars.FindAsync(new object[] { avatar.Id }, ct);
        if (existing == null)
            _db.Avatars.Add((Avatar)avatar);
        else
            _db.Entry(existing).CurrentValues.SetValues((Avatar)avatar);

        await _db.SaveChangesAsync(ct);
        return new OASISResult<IAvatar> { Result = avatar, Message = "Saved." };
    }

    public async Task<OASISResult<bool>> DeleteAvatarAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await _db.Avatars.FindAsync(new object[] { id }, ct);
        if (existing == null)
            return new OASISResult<bool> { IsError = true, Message = "Avatar not found.", Result = false };

        _db.Avatars.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return new OASISResult<bool> { Result = true, Message = "Deleted." };
    }

    public async Task<OASISResult<IEnumerable<IAvatar>>> LoadAllAvatarsAsync(CancellationToken ct = default)
    {
        var list = await _db.Avatars.AsNoTracking().ToListAsync(ct);
        return new OASISResult<IEnumerable<IAvatar>> { Result = list, Message = "Success" };
    }

    // Wallet
    public async Task<OASISResult<IWallet>> LoadWalletAsync(Guid id, CancellationToken ct = default)
    {
        var wallet = await _db.Wallets.FindAsync(new object[] { id }, ct);
        return new OASISResult<IWallet>
        {
            IsError = wallet == null,
            Message = wallet == null ? "Wallet not found." : "Success",
            Result = wallet
        };
    }

    public async Task<OASISResult<IWallet>> SaveWalletAsync(IWallet wallet, CancellationToken ct = default)
    {
        var existing = await _db.Wallets.FindAsync(new object[] { wallet.Id }, ct);
        if (existing == null)
            _db.Wallets.Add((Wallet)wallet);
        else
            _db.Entry(existing).CurrentValues.SetValues((Wallet)wallet);

        await _db.SaveChangesAsync(ct);
        return new OASISResult<IWallet> { Result = wallet, Message = "Saved." };
    }

    public async Task<OASISResult<bool>> DeleteWalletAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await _db.Wallets.FindAsync(new object[] { id }, ct);
        if (existing == null)
            return new OASISResult<bool> { IsError = true, Message = "Wallet not found.", Result = false };

        _db.Wallets.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return new OASISResult<bool> { Result = true, Message = "Deleted." };
    }

    public async Task<OASISResult<IEnumerable<IWallet>>> LoadWalletsByAvatarAsync(Guid avatarId, CancellationToken ct = default)
    {
        var list = await _db.Wallets.AsNoTracking().Where(w => w.AvatarId == avatarId).ToListAsync(ct);
        return new OASISResult<IEnumerable<IWallet>> { Result = list, Message = "Success" };
    }

    public async Task<OASISResult<IEnumerable<IWallet>>> LoadAllWalletsAsync(CancellationToken ct = default)
    {
        var list = await _db.Wallets.AsNoTracking().ToListAsync(ct);
        return new OASISResult<IEnumerable<IWallet>> { Result = list, Message = "Success" };
    }

    // Holon
    public async Task<OASISResult<IHolon>> LoadHolonAsync(Guid id, CancellationToken ct = default)
    {
        var holon = await _db.Holons.FindAsync(new object[] { id }, ct);
        return new OASISResult<IHolon>
        {
            IsError = holon == null,
            Message = holon == null ? "Holon not found." : "Success",
            Result = holon
        };
    }

    public async Task<OASISResult<IHolon>> SaveHolonAsync(IHolon holon, CancellationToken ct = default)
    {
        var existing = await _db.Holons.FindAsync(new object[] { holon.Id }, ct);
        if (existing == null)
            _db.Holons.Add((Holon)holon);
        else
            _db.Entry(existing).CurrentValues.SetValues((Holon)holon);

        await _db.SaveChangesAsync(ct);
        return new OASISResult<IHolon> { Result = holon, Message = "Saved." };
    }

    public async Task<OASISResult<bool>> DeleteHolonAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await _db.Holons.FindAsync(new object[] { id }, ct);
        if (existing == null)
            return new OASISResult<bool> { IsError = true, Message = "Holon not found.", Result = false };

        _db.Holons.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return new OASISResult<bool> { Result = true, Message = "Deleted." };
    }

    public async Task<OASISResult<IEnumerable<IHolon>>> LoadAllHolonsAsync(HolonQueryRequest? query = null, CancellationToken ct = default)
    {
        var q = _db.Holons.AsNoTracking().AsQueryable();

        if (query != null)
        {
            if (!string.IsNullOrEmpty(query.Name))
                q = q.Where(h => h.Name.Contains(query.Name));
            if (query.AvatarId.HasValue)
                q = q.Where(h => h.AvatarId == query.AvatarId);
            if (!string.IsNullOrEmpty(query.ProviderName))
                q = q.Where(h => h.ProviderName == query.ProviderName);
            if (!string.IsNullOrEmpty(query.ChainId))
                q = q.Where(h => h.ChainId == query.ChainId);
            if (!string.IsNullOrEmpty(query.AssetType))
                q = q.Where(h => h.AssetType == query.AssetType);
            if (query.IsActive.HasValue)
                q = q.Where(h => h.IsActive == query.IsActive.Value);
            if (query.ParentHolonId.HasValue)
                q = q.Where(h => h.ParentHolonId == query.ParentHolonId.Value);
        }

        var list = await q.ToListAsync(ct);
        return new OASISResult<IEnumerable<IHolon>> { Result = list, Message = "Success" };
    }

    // Blockchain Operation
    public async Task<OASISResult<IBlockchainOperation>> LoadBlockchainOperationAsync(Guid id, CancellationToken ct = default)
    {
        var op = await _db.BlockchainOperations.FindAsync(new object[] { id }, ct);
        return new OASISResult<IBlockchainOperation>
        {
            IsError = op == null,
            Message = op == null ? "Operation not found." : "Success",
            Result = op
        };
    }

    public async Task<OASISResult<IBlockchainOperation>> SaveBlockchainOperationAsync(IBlockchainOperation operation, CancellationToken ct = default)
    {
        var existing = await _db.BlockchainOperations.FindAsync(new object[] { operation.Id }, ct);
        if (existing == null)
            _db.BlockchainOperations.Add((BlockchainOperation)operation);
        else
            _db.Entry(existing).CurrentValues.SetValues((BlockchainOperation)operation);

        await _db.SaveChangesAsync(ct);
        return new OASISResult<IBlockchainOperation> { Result = operation, Message = "Saved." };
    }

    public async Task<OASISResult<bool>> DeleteBlockchainOperationAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await _db.BlockchainOperations.FindAsync(new object[] { id }, ct);
        if (existing == null)
            return new OASISResult<bool> { IsError = true, Message = "Operation not found.", Result = false };

        _db.BlockchainOperations.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return new OASISResult<bool> { Result = true, Message = "Deleted." };
    }

    public async Task<OASISResult<IEnumerable<IBlockchainOperation>>> LoadBlockchainOperationsByAvatarAsync(Guid avatarId, CancellationToken ct = default)
    {
        var list = await _db.BlockchainOperations.AsNoTracking().Where(o => o.AvatarId == avatarId).ToListAsync(ct);
        return new OASISResult<IEnumerable<IBlockchainOperation>> { Result = list, Message = "Success" };
    }

    // STAR ODK
    public async Task<OASISResult<ISTARODK>> LoadSTARODKAsync(Guid id, CancellationToken ct = default)
    {
        var odk = await _db.STARODKs.FindAsync(new object[] { id }, ct);
        return new OASISResult<ISTARODK>
        {
            IsError = odk == null,
            Message = odk == null ? "STAR ODK not found." : "Success",
            Result = odk
        };
    }

    public async Task<OASISResult<ISTARODK>> SaveSTARODKAsync(ISTARODK odk, CancellationToken ct = default)
    {
        var existing = await _db.STARODKs.FindAsync(new object[] { odk.Id }, ct);
        if (existing == null)
            _db.STARODKs.Add((STARODK)odk);
        else
            _db.Entry(existing).CurrentValues.SetValues((STARODK)odk);

        await _db.SaveChangesAsync(ct);
        return new OASISResult<ISTARODK> { Result = odk, Message = "Saved." };
    }

    public async Task<OASISResult<bool>> DeleteSTARODKAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await _db.STARODKs.FindAsync(new object[] { id }, ct);
        if (existing == null)
            return new OASISResult<bool> { IsError = true, Message = "STAR ODK not found.", Result = false };

        _db.STARODKs.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return new OASISResult<bool> { Result = true, Message = "Deleted." };
    }

    public async Task<OASISResult<IEnumerable<ISTARODK>>> LoadAllSTARODKsAsync(CancellationToken ct = default)
    {
        var list = await _db.STARODKs.AsNoTracking().ToListAsync(ct);
        return new OASISResult<IEnumerable<ISTARODK>> { Result = list, Message = "Success" };
    }

    // Avatar NFT Management
    public async Task<OASISResult<IAvatarNFT>> SaveAvatarNFTAsync(IAvatarNFT avatarNFT, CancellationToken ct = default)
    {
        var existing = await _db.AvatarNFTs.FindAsync(new object[] { avatarNFT.Id }, ct);
        if (existing == null)
            _db.AvatarNFTs.Add((AvatarNFT)avatarNFT);
        else
            _db.Entry(existing).CurrentValues.SetValues((AvatarNFT)avatarNFT);

        await _db.SaveChangesAsync(ct);
        return new OASISResult<IAvatarNFT> { Result = avatarNFT, Message = "Saved." };
    }

    public async Task<OASISResult<IAvatarNFT>> LoadAvatarNFTAsync(Guid id, CancellationToken ct = default)
    {
        var nft = await _db.AvatarNFTs.FindAsync(new object[] { id }, ct);
        return new OASISResult<IAvatarNFT>
        {
            IsError = nft == null,
            Message = nft == null ? "NFT not found." : "Success",
            Result = nft
        };
    }

    public async Task<OASISResult<IAvatarNFT>> LoadAvatarNFTByTokenIdAsync(string chainType, string nftContractAddress, string tokenId, CancellationToken ct = default)
    {
        var nft = await _db.AvatarNFTs
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.ChainType == chainType && 
                                   n.NFTContractAddress == nftContractAddress && 
                                   n.TokenId == tokenId, ct);
        return new OASISResult<IAvatarNFT>
        {
            IsError = nft == null,
            Message = nft == null ? "NFT not found." : "Success",
            Result = nft
        };
    }

    public async Task<OASISResult<IEnumerable<IAvatarNFT>>> LoadAvatarNFTsByAvatarAsync(Guid avatarId, CancellationToken ct = default)
    {
        var list = await _db.AvatarNFTs.AsNoTracking().Where(n => n.AvatarId == avatarId).ToListAsync(ct);
        return new OASISResult<IEnumerable<IAvatarNFT>> { Result = list, Message = "Success" };
    }

    public async Task<OASISResult<bool>> DeleteAvatarNFTAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await _db.AvatarNFTs.FindAsync(new object[] { id }, ct);
        if (existing == null)
            return new OASISResult<bool> { IsError = true, Message = "NFT not found.", Result = false };

        _db.AvatarNFTs.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return new OASISResult<bool> { Result = true, Message = "Deleted." };
    }

    // Holon NFT Binding Management
    public async Task<OASISResult<IHolonNFTBinding>> SaveHolonNFTBindingAsync(IHolonNFTBinding binding, CancellationToken ct = default)
    {
        var existing = await _db.HolonNFTBindings.FindAsync(new object[] { binding.Id }, ct);
        if (existing == null)
            _db.HolonNFTBindings.Add((HolonNFTBinding)binding);
        else
            _db.Entry(existing).CurrentValues.SetValues((HolonNFTBinding)binding);

        await _db.SaveChangesAsync(ct);
        return new OASISResult<IHolonNFTBinding> { Result = binding, Message = "Saved." };
    }

    public async Task<OASISResult<IHolonNFTBinding>> LoadHolonNFTBindingAsync(Guid id, CancellationToken ct = default)
    {
        var binding = await _db.HolonNFTBindings.FindAsync(new object[] { id }, ct);
        return new OASISResult<IHolonNFTBinding>
        {
            IsError = binding == null,
            Message = binding == null ? "Binding not found." : "Success",
            Result = binding
        };
    }

    public async Task<OASISResult<IEnumerable<IHolonNFTBinding>>> LoadHolonNFTBindingsByAvatarNFTAsync(Guid avatarNFTId, CancellationToken ct = default)
    {
        var list = await _db.HolonNFTBindings.AsNoTracking().Where(b => b.AvatarNFTId == avatarNFTId).ToListAsync(ct);
        return new OASISResult<IEnumerable<IHolonNFTBinding>> { Result = list, Message = "Success" };
    }

    public async Task<OASISResult<bool>> DeleteHolonNFTBindingAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await _db.HolonNFTBindings.FindAsync(new object[] { id }, ct);
        if (existing == null)
            return new OASISResult<bool> { IsError = true, Message = "Binding not found.", Result = false };

        _db.HolonNFTBindings.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return new OASISResult<bool> { Result = true, Message = "Deleted." };
    }

    // Wallet NFT Binding Management
    public async Task<OASISResult<IWalletNFTBinding>> SaveWalletNFTBindingAsync(IWalletNFTBinding binding, CancellationToken ct = default)
    {
        var existing = await _db.WalletNFTBindings.FindAsync(new object[] { binding.Id }, ct);
        if (existing == null)
            _db.WalletNFTBindings.Add((WalletNFTBinding)binding);
        else
            _db.Entry(existing).CurrentValues.SetValues((WalletNFTBinding)binding);

        await _db.SaveChangesAsync(ct);
        return new OASISResult<IWalletNFTBinding> { Result = binding, Message = "Saved." };
    }

    public async Task<OASISResult<IWalletNFTBinding>> LoadWalletNFTBindingAsync(Guid id, CancellationToken ct = default)
    {
        var binding = await _db.WalletNFTBindings.FindAsync(new object[] { id }, ct);
        return new OASISResult<IWalletNFTBinding>
        {
            IsError = binding == null,
            Message = binding == null ? "Binding not found." : "Success",
            Result = binding
        };
    }

    public async Task<OASISResult<IEnumerable<IWalletNFTBinding>>> LoadWalletNFTBindingsByAvatarNFTAsync(Guid avatarNFTId, CancellationToken ct = default)
    {
        var list = await _db.WalletNFTBindings.AsNoTracking().Where(b => b.AvatarNFTId == avatarNFTId).ToListAsync(ct);
        return new OASISResult<IEnumerable<IWalletNFTBinding>> { Result = list, Message = "Success" };
    }

    public async Task<OASISResult<bool>> DeleteWalletNFTBindingAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await _db.WalletNFTBindings.FindAsync(new object[] { id }, ct);
        if (existing == null)
            return new OASISResult<bool> { IsError = true, Message = "Binding not found.", Result = false };

        _db.WalletNFTBindings.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return new OASISResult<bool> { Result = true, Message = "Deleted." };
    }

    // Quest
    public async Task<OASISResult<Quest>> SaveQuestAsync(Quest quest, CancellationToken ct = default)
    {
        var existing = await _db.Quests
            .Include(q => q.Nodes)
            .Include(q => q.Edges)
            .Include(q => q.Dependencies)
            .FirstOrDefaultAsync(q => q.Id == quest.Id, ct);

        if (existing == null)
        {
            _db.Quests.Add(quest);
        }
        else
        {
            _db.Entry(existing).CurrentValues.SetValues(quest);

            // Sync child collections
            _db.QuestNodes.RemoveRange(existing.Nodes);
            _db.QuestEdges.RemoveRange(existing.Edges);
            _db.QuestDependencies.RemoveRange(existing.Dependencies);

            foreach (var node in quest.Nodes) { node.QuestId = quest.Id; _db.QuestNodes.Add(node); }
            foreach (var edge in quest.Edges) { edge.QuestId = quest.Id; _db.QuestEdges.Add(edge); }
            foreach (var dep in quest.Dependencies) { dep.QuestId = quest.Id; _db.QuestDependencies.Add(dep); }
        }

        await _db.SaveChangesAsync(ct);
        return new OASISResult<Quest> { Result = quest, Message = "Saved." };
    }

    public async Task<OASISResult<Quest>> LoadQuestAsync(Guid id, CancellationToken ct = default)
    {
        var quest = await _db.Quests
            .Include(q => q.Nodes)
            .Include(q => q.Edges)
            .Include(q => q.Dependencies)
            .FirstOrDefaultAsync(q => q.Id == id, ct);

        return new OASISResult<Quest>
        {
            IsError = quest == null,
            Message = quest == null ? "Quest not found." : "Success",
            Result = quest
        };
    }

    public async Task<OASISResult<IEnumerable<Quest>>> LoadQuestsByAvatarAsync(Guid avatarId, CancellationToken ct = default)
    {
        var list = await _db.Quests
            .Include(q => q.Nodes)
            .Include(q => q.Edges)
            .Include(q => q.Dependencies)
            .Where(q => q.AvatarId == avatarId)
            .ToListAsync(ct);

        return new OASISResult<IEnumerable<Quest>> { Result = list, Message = "Success" };
    }

    public async Task<OASISResult<bool>> DeleteQuestAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await _db.Quests
            .Include(q => q.Nodes)
            .Include(q => q.Edges)
            .Include(q => q.Dependencies)
            .FirstOrDefaultAsync(q => q.Id == id, ct);

        if (existing == null)
            return new OASISResult<bool> { IsError = true, Message = "Quest not found.", Result = false };

        _db.Quests.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return new OASISResult<bool> { Result = true, Message = "Deleted." };
    }

    // Quest Template
    public async Task<OASISResult<QuestTemplate>> SaveQuestTemplateAsync(QuestTemplate template, CancellationToken ct = default)
    {
        var existing = await _db.QuestTemplates
            .Include(t => t.Nodes)
            .Include(t => t.Edges)
            .FirstOrDefaultAsync(t => t.Id == template.Id, ct);

        if (existing == null)
        {
            _db.QuestTemplates.Add(template);
        }
        else
        {
            _db.Entry(existing).CurrentValues.SetValues(template);
            _db.QuestTemplateNodes.RemoveRange(existing.Nodes);
            _db.QuestTemplateEdges.RemoveRange(existing.Edges);

            foreach (var node in template.Nodes) { node.TemplateId = template.Id; _db.QuestTemplateNodes.Add(node); }
            foreach (var edge in template.Edges) { edge.TemplateId = template.Id; _db.QuestTemplateEdges.Add(edge); }
        }

        await _db.SaveChangesAsync(ct);
        return new OASISResult<QuestTemplate> { Result = template, Message = "Saved." };
    }

    public async Task<OASISResult<QuestTemplate>> LoadQuestTemplateAsync(Guid id, CancellationToken ct = default)
    {
        var template = await _db.QuestTemplates
            .Include(t => t.Nodes)
            .Include(t => t.Edges)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        return new OASISResult<QuestTemplate>
        {
            IsError = template == null,
            Message = template == null ? "Quest template not found." : "Success",
            Result = template
        };
    }

    public async Task<OASISResult<IEnumerable<QuestTemplate>>> LoadAllQuestTemplatesAsync(CancellationToken ct = default)
    {
        var list = await _db.QuestTemplates
            .Include(t => t.Nodes)
            .Include(t => t.Edges)
            .AsNoTracking()
            .ToListAsync(ct);

        return new OASISResult<IEnumerable<QuestTemplate>> { Result = list, Message = "Success" };
    }

    public async Task<OASISResult<bool>> DeleteQuestTemplateAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await _db.QuestTemplates
            .Include(t => t.Nodes)
            .Include(t => t.Edges)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        if (existing == null)
            return new OASISResult<bool> { IsError = true, Message = "Quest template not found.", Result = false };

        _db.QuestTemplates.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return new OASISResult<bool> { Result = true, Message = "Deleted." };
    }

    // Quest Node Template
    public async Task<OASISResult<QuestNodeTemplate>> SaveQuestNodeTemplateAsync(QuestNodeTemplate template, CancellationToken ct = default)
    {
        var existing = await _db.QuestNodeTemplates.FindAsync(new object[] { template.Id }, ct);
        if (existing == null)
            _db.QuestNodeTemplates.Add(template);
        else
            _db.Entry(existing).CurrentValues.SetValues(template);

        await _db.SaveChangesAsync(ct);
        return new OASISResult<QuestNodeTemplate> { Result = template, Message = "Saved." };
    }

    public async Task<OASISResult<IEnumerable<QuestNodeTemplate>>> LoadAllQuestNodeTemplatesAsync(CancellationToken ct = default)
    {
        var list = await _db.QuestNodeTemplates.AsNoTracking().ToListAsync(ct);
        return new OASISResult<IEnumerable<QuestNodeTemplate>> { Result = list, Message = "Success" };
    }
}
