using Microsoft.EntityFrameworkCore;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.Interfaces;
using QuestEntity = OASIS.WebAPI.Models.Quest.Quest;
using QuestNode = OASIS.WebAPI.Models.Quest.QuestNode;
using QuestEdge = OASIS.WebAPI.Models.Quest.QuestEdge;
using QuestDependency = OASIS.WebAPI.Models.Quest.QuestDependency;
using QuestNodeTemplate = OASIS.WebAPI.Models.Quest.QuestNodeTemplate;
using QuestTemplate = OASIS.WebAPI.Models.Quest.QuestTemplate;
using QuestTemplateNode = OASIS.WebAPI.Models.Quest.QuestTemplateNode;
using QuestTemplateEdge = OASIS.WebAPI.Models.Quest.QuestTemplateEdge;

namespace OASIS.WebAPI.Services.Quest;

/// <summary>
/// EF Core implementation of IQuestRepository wrapping OASISDbContext.
/// </summary>
public class QuestRepository : IQuestRepository
{
    private readonly OASISDbContext _dbContext;

    public QuestRepository(OASISDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<QuestEntity?> GetByIdAsync(Guid id)
    {
        return await _dbContext.Quests
            .Include(q => q.Nodes)
            .Include(q => q.Edges)
            .Include(q => q.Dependencies)
            .FirstOrDefaultAsync(q => q.Id == id);
    }

    public async Task<IEnumerable<QuestEntity>> GetByAvatarIdAsync(Guid avatarId)
    {
        return await _dbContext.Quests
            .Include(q => q.Nodes)
            .Include(q => q.Edges)
            .Where(q => q.AvatarId == avatarId)
            .ToListAsync();
    }

    public async Task<IEnumerable<QuestEntity>> GetByDappSeriesIdAsync(Guid dappSeriesId)
    {
        return await _dbContext.Quests
            .Include(q => q.Nodes)
            .Include(q => q.Edges)
            .Where(q => q.DappSeriesId == dappSeriesId)
            .ToListAsync();
    }

    public async Task<QuestEntity> CreateAsync(QuestEntity quest)
    {
        _dbContext.Quests.Add(quest);
        await _dbContext.SaveChangesAsync();
        return quest;
    }

    public async Task<QuestEntity> UpdateAsync(QuestEntity quest)
    {
        _dbContext.Quests.Update(quest);
        await _dbContext.SaveChangesAsync();
        return quest;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var quest = await _dbContext.Quests.FindAsync(id);
        if (quest == null) return false;

        _dbContext.Quests.Remove(quest);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<IEnumerable<QuestNodeTemplate>> GetNodeTemplatesAsync(bool? publicOnly = null)
    {
        var query = _dbContext.QuestNodeTemplates.AsQueryable();
        if (publicOnly == true)
        {
            query = query.Where(t => t.IsPublic);
        }
        return await query.ToListAsync();
    }

    public async Task<QuestNodeTemplate?> GetNodeTemplateByIdAsync(Guid id)
    {
        return await _dbContext.QuestNodeTemplates.FindAsync(id);
    }

    public async Task<QuestNodeTemplate> CreateNodeTemplateAsync(QuestNodeTemplate template)
    {
        _dbContext.QuestNodeTemplates.Add(template);
        await _dbContext.SaveChangesAsync();
        return template;
    }

    public async Task<IEnumerable<QuestTemplate>> GetQuestTemplatesAsync(bool? publicOnly = null)
    {
        var query = _dbContext.QuestTemplates
            .Include(t => t.Nodes)
            .Include(t => t.Edges)
            .AsQueryable();
        if (publicOnly == true)
        {
            query = query.Where(t => t.IsPublic);
        }
        return await query.ToListAsync();
    }

    public async Task<QuestTemplate?> GetQuestTemplateByIdAsync(Guid id)
    {
        return await _dbContext.QuestTemplates
            .Include(t => t.Nodes)
            .Include(t => t.Edges)
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<QuestTemplate> CreateQuestTemplateAsync(QuestTemplate template)
    {
        _dbContext.QuestTemplates.Add(template);
        await _dbContext.SaveChangesAsync();
        return template;
    }
}
