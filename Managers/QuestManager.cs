using System.Text.Json;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

// NftQueryRequest lives in Models.Responses alongside NftResult

namespace OASIS.WebAPI.Managers;

public class QuestManager : IQuestManager
{
    private readonly ProviderContext _providerContext;
    private readonly IQuestDagValidator _dagValidator;
    private readonly IHolonManager _holonManager;
    private readonly INftManager _nftManager;
    private readonly IWalletManager _walletManager;
    private readonly ISTARManager _starManager;
    private readonly ISearchManager _searchManager;
    private readonly IBlockchainOperationManager _blockchainManager;
    private readonly IAvatarNFTService _avatarNFTService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public QuestManager(
        ProviderContext providerContext,
        IQuestDagValidator dagValidator,
        IHolonManager holonManager,
        INftManager nftManager,
        IWalletManager walletManager,
        ISTARManager starManager,
        ISearchManager searchManager,
        IBlockchainOperationManager blockchainManager,
        IAvatarNFTService avatarNFTService)
    {
        _providerContext = providerContext;
        _dagValidator = dagValidator;
        _holonManager = holonManager;
        _nftManager = nftManager;
        _walletManager = walletManager;
        _starManager = starManager;
        _searchManager = searchManager;
        _blockchainManager = blockchainManager;
        _avatarNFTService = avatarNFTService;
    }

    // ═══════════════════════════════════════════════════════════════════
    // QUEST CRUD
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<Quest>> CreateAsync(QuestCreateModel model, Guid avatarId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<Quest> { IsError = true, Message = activation.Message };

        var quest = new Quest
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            Description = model.Description,
            AvatarId = avatarId,
            Status = QuestStatus.Draft,
            CreatedDate = DateTime.UtcNow
        };

        // Create nodes with new Ids
        var nodeIds = new List<Guid>();
        foreach (var nodeModel in model.Nodes)
        {
            var node = new QuestNode
            {
                Id = Guid.NewGuid(),
                QuestId = quest.Id,
                Name = nodeModel.Name,
                NodeType = nodeModel.NodeType,
                Config = nodeModel.Config,
                IsEntry = nodeModel.IsEntry,
                IsTerminal = nodeModel.IsTerminal,
                NodeTemplateId = nodeModel.NodeTemplateId,
                State = QuestNodeState.Pending
            };
            quest.Nodes.Add(node);
            nodeIds.Add(node.Id);
        }

        // Map edge indices to node Ids
        foreach (var edgeModel in model.Edges)
        {
            if (edgeModel.SourceNodeId < 0 || edgeModel.SourceNodeId >= nodeIds.Count ||
                edgeModel.TargetNodeId < 0 || edgeModel.TargetNodeId >= nodeIds.Count)
            {
                return new OASISResult<Quest> { IsError = true, Message = $"Edge index out of range. Source={edgeModel.SourceNodeId}, Target={edgeModel.TargetNodeId}, NodeCount={nodeIds.Count}." };
            }

            var edge = new QuestEdge
            {
                Id = Guid.NewGuid(),
                QuestId = quest.Id,
                SourceNodeId = nodeIds[edgeModel.SourceNodeId],
                TargetNodeId = nodeIds[edgeModel.TargetNodeId],
                Condition = edgeModel.Condition,
                EdgeType = edgeModel.EdgeType
            };
            quest.Edges.Add(edge);
        }

        return await _providerContext.CurrentProvider.SaveQuestAsync(quest);
    }

    public async Task<OASISResult<Quest>> GetAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<Quest> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadQuestAsync(id);
    }

    public async Task<OASISResult<IEnumerable<Quest>>> GetByAvatarAsync(Guid avatarId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<Quest>> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadQuestsByAvatarAsync(avatarId);
    }

    public async Task<OASISResult<Quest>> UpdateAsync(Guid id, QuestUpdateModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<Quest> { IsError = true, Message = activation.Message };

        var existing = await _providerContext.CurrentProvider.LoadQuestAsync(id);
        if (existing.IsError || existing.Result == null) return existing;

        var quest = existing.Result;
        if (model.Name != null) quest.Name = model.Name;
        if (model.Description != null) quest.Description = model.Description;
        if (model.Status.HasValue)
        {
            quest.Status = model.Status.Value;
            if (model.Status.Value == QuestStatus.Completed)
                quest.CompletedDate = DateTime.UtcNow;
        }

        return await _providerContext.CurrentProvider.SaveQuestAsync(quest);
    }

    public async Task<OASISResult<bool>> DeleteAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.DeleteQuestAsync(id);
    }

    // ═══════════════════════════════════════════════════════════════════
    // DAG VALIDATION
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<bool>> ValidateDAGAsync(Guid questId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var questResult = await _providerContext.CurrentProvider.LoadQuestAsync(questId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        var validation = _dagValidator.Validate(quest);

        if (!validation.IsValid)
        {
            return new OASISResult<bool>
            {
                IsError = true,
                Result = false,
                Message = $"DAG validation failed: {string.Join("; ", validation.Errors)}"
            };
        }

        // Apply topological order to nodes
        var orderMap = new Dictionary<Guid, int>();
        for (int i = 0; i < validation.TopologicalOrder.Count; i++)
            orderMap[validation.TopologicalOrder[i]] = i;

        foreach (var node in quest.Nodes)
        {
            if (orderMap.TryGetValue(node.Id, out var order))
                node.ExecutionOrder = order;
        }

        await _providerContext.CurrentProvider.SaveQuestAsync(quest);

        return new OASISResult<bool> { Result = true, Message = "DAG is valid." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // EXECUTION
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<Quest>> ExecuteAsync(Guid questId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<Quest> { IsError = true, Message = activation.Message };

        // Validate DAG first
        var validationResult = await ValidateDAGAsync(questId, request);
        if (validationResult.IsError)
            return new OASISResult<Quest> { IsError = true, Message = validationResult.Message };

        var questResult = await _providerContext.CurrentProvider.LoadQuestAsync(questId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<Quest> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        quest.Status = QuestStatus.Active;

        // Execute nodes in topological order
        var sortedNodes = quest.Nodes.OrderBy(n => n.ExecutionOrder).ToList();

        foreach (var node in sortedNodes)
        {
            // Check conditional edges — skip node if any incoming conditional edge evaluates to false
            var incomingEdges = quest.Edges.Where(e => e.TargetNodeId == node.Id).ToList();
            var shouldSkip = false;

            foreach (var edge in incomingEdges)
            {
                if (edge.EdgeType == QuestEdgeType.Conditional && !string.IsNullOrEmpty(edge.Condition))
                {
                    var sourceNode = quest.Nodes.FirstOrDefault(n => n.Id == edge.SourceNodeId);
                    if (sourceNode?.State == QuestNodeState.Failed || sourceNode?.State == QuestNodeState.Skipped)
                    {
                        shouldSkip = true;
                        break;
                    }
                }

                // If source node failed on a control edge, skip this node
                if (edge.EdgeType == QuestEdgeType.Control)
                {
                    var sourceNode = quest.Nodes.FirstOrDefault(n => n.Id == edge.SourceNodeId);
                    if (sourceNode?.State == QuestNodeState.Failed)
                    {
                        shouldSkip = true;
                        break;
                    }
                }
            }

            if (shouldSkip)
            {
                node.State = QuestNodeState.Skipped;
                continue;
            }

            try
            {
                var nodeResult = await ExecuteNodeInternalAsync(quest, node);
                if (nodeResult.IsError)
                {
                    node.State = QuestNodeState.Failed;
                    node.Error = nodeResult.Message;
                }
                else
                {
                    node.State = QuestNodeState.Succeeded;
                    node.Output = nodeResult.Result?.Output;
                }
            }
            catch (Exception ex)
            {
                node.State = QuestNodeState.Failed;
                node.Error = ex.Message;
            }
        }

        // Determine overall quest status
        if (quest.Nodes.Any(n => n.State == QuestNodeState.Failed))
            quest.Status = QuestStatus.Failed;
        else
        {
            quest.Status = QuestStatus.Completed;
            quest.CompletedDate = DateTime.UtcNow;
        }

        await _providerContext.CurrentProvider.SaveQuestAsync(quest);
        return new OASISResult<Quest> { Result = quest, Message = $"Quest execution {quest.Status}." };
    }

    public async Task<OASISResult<QuestNode>> ExecuteNodeAsync(Guid questId, Guid nodeId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<QuestNode> { IsError = true, Message = activation.Message };

        var questResult = await _providerContext.CurrentProvider.LoadQuestAsync(questId);
        if (questResult.IsError || questResult.Result == null)
            return new OASISResult<QuestNode> { IsError = true, Message = questResult.Message };

        var quest = questResult.Result;
        var node = quest.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
            return new OASISResult<QuestNode> { IsError = true, Message = "Node not found." };

        var result = await ExecuteNodeInternalAsync(quest, node);

        await _providerContext.CurrentProvider.SaveQuestAsync(quest);
        return result;
    }

    private async Task<OASISResult<QuestNode>> ExecuteNodeInternalAsync(Quest quest, QuestNode node)
    {
        node.State = QuestNodeState.Running;

        try
        {
            string? outputJson = null;

            switch (node.NodeType)
            {
                // ─── Holon operations ───
                case QuestNodeType.HolonCreate:
                {
                    var model = JsonSerializer.Deserialize<HolonCreateModel>(node.Config, JsonOptions)!;
                    var r = await _holonManager.CreateAsync(model, quest.AvatarId);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.HolonUpdate:
                {
                    var cfg = JsonSerializer.Deserialize<HolonUpdateNodeConfig>(node.Config, JsonOptions)!;
                    var r = await _holonManager.UpdateAsync(cfg.HolonId, cfg.Model);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.HolonDelete:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _holonManager.DeleteAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.HolonGet:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _holonManager.GetAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.HolonQuery:
                {
                    var query = JsonSerializer.Deserialize<HolonQueryRequest>(node.Config, JsonOptions)!;
                    var r = await _holonManager.QueryAsync(query);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.HolonInteract:
                {
                    var cfg = JsonSerializer.Deserialize<HolonInteractNodeConfig>(node.Config, JsonOptions)!;
                    var r = await _holonManager.InteractAsync(cfg.HolonId, cfg.Request);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.HolonGetChildren:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _holonManager.GetChildrenAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.HolonGetPeers:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _holonManager.GetPeersAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.HolonGetAncestors:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _holonManager.GetAncestorsAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.HolonGetDescendants:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _holonManager.GetDescendantsAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.HolonPropagate:
                {
                    var cfg = JsonSerializer.Deserialize<HolonPropagateNodeConfig>(node.Config, JsonOptions)!;
                    var r = await _holonManager.PropagateAsync(cfg.HolonId, cfg.Request);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.HolonCompose:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _holonManager.ComposeAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.HolonClone:
                {
                    var cfg = JsonSerializer.Deserialize<HolonCloneNodeConfig>(node.Config, JsonOptions)!;
                    var r = await _holonManager.CloneAsync(cfg.HolonId, cfg.Request, quest.AvatarId);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.HolonMoveSubtree:
                {
                    var cfg = JsonSerializer.Deserialize<HolonMoveNodeConfig>(node.Config, JsonOptions)!;
                    var r = await _holonManager.MoveSubtreeAsync(cfg.HolonId, cfg.NewParentId);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }

                // ─── NFT operations ───
                case QuestNodeType.NftMint:
                {
                    var model = JsonSerializer.Deserialize<NftMintRequest>(node.Config, JsonOptions)!;
                    var r = await _nftManager.MintAsync(model, quest.AvatarId);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.NftTransfer:
                {
                    var cfg = JsonSerializer.Deserialize<NftTransferNodeConfig>(node.Config, JsonOptions)!;
                    var r = await _nftManager.TransferAsync(cfg.NftId, cfg.Request, quest.AvatarId);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.NftBurn:
                {
                    var cfg = JsonSerializer.Deserialize<NftBurnNodeConfig>(node.Config, JsonOptions)!;
                    var r = await _nftManager.BurnAsync(cfg.NftId, cfg.WalletId, quest.AvatarId);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.NftGet:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _nftManager.GetAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.NftQuery:
                {
                    var query = JsonSerializer.Deserialize<NftQueryRequest>(node.Config, JsonOptions)!;
                    var r = await _nftManager.QueryAsync(query);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.NftGetMetadata:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _nftManager.GetMetadataAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }

                // ─── Wallet operations ───
                case QuestNodeType.WalletCreate:
                {
                    var model = JsonSerializer.Deserialize<WalletCreateModel>(node.Config, JsonOptions)!;
                    var r = await _walletManager.CreateAsync(model, quest.AvatarId);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.WalletUpdate:
                {
                    var cfg = JsonSerializer.Deserialize<WalletUpdateNodeConfig>(node.Config, JsonOptions)!;
                    var r = await _walletManager.UpdateAsync(cfg.WalletId, cfg.Model);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.WalletDelete:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _walletManager.DeleteAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.WalletGet:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _walletManager.GetAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.WalletQuery:
                {
                    var query = JsonSerializer.Deserialize<WalletQueryRequest>(node.Config, JsonOptions)!;
                    var r = await _walletManager.QueryAsync(query);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.WalletSetDefault:
                {
                    var cfg = JsonSerializer.Deserialize<WalletSetDefaultNodeConfig>(node.Config, JsonOptions)!;
                    var r = await _walletManager.SetDefaultAsync(quest.AvatarId, cfg.WalletId);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.WalletGetPortfolio:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _walletManager.GetPortfolioAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }

                // ─── STAR operations ───
                case QuestNodeType.StarGenerate:
                {
                    var cfg = JsonSerializer.Deserialize<StarGenerateNodeConfig>(node.Config, JsonOptions)!;
                    var r = await _starManager.GenerateAsync(cfg.StarId, cfg.Request);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }
                case QuestNodeType.StarDeploy:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _starManager.DeployAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }

                // ─── Search ───
                case QuestNodeType.Search:
                {
                    var searchReq = JsonSerializer.Deserialize<SearchRequest>(node.Config, JsonOptions)!;
                    var r = await _searchManager.SearchAsync(searchReq);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }

                // ─── Avatar NFT ───
                case QuestNodeType.AvatarNFTGetComposite:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _avatarNFTService.GetAvatarNFTCompositeAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }

                // ─── Blockchain ───
                case QuestNodeType.BlockchainExecute:
                {
                    var cfg = JsonSerializer.Deserialize<IdConfig>(node.Config, JsonOptions)!;
                    var r = await _blockchainManager.GetAsync(cfg.Id);
                    outputJson = JsonSerializer.Serialize(r, JsonOptions);
                    if (r.IsError) return Fail(node, r.Message);
                    break;
                }

                // ─── Control-flow ───
                case QuestNodeType.Condition:
                {
                    // Condition nodes evaluate to a pass-through; the edge conditions
                    // on outgoing edges handle the actual branching.
                    outputJson = node.Config;
                    break;
                }
                case QuestNodeType.ComposeOutputs:
                {
                    // Gather outputs from all upstream nodes
                    var incomingNodeIds = quest.Edges
                        .Where(e => e.TargetNodeId == node.Id)
                        .Select(e => e.SourceNodeId)
                        .ToHashSet();
                    var upstreamOutputs = quest.Nodes
                        .Where(n => incomingNodeIds.Contains(n.Id) && n.Output != null)
                        .ToDictionary(n => n.Name, n => n.Output!);
                    outputJson = JsonSerializer.Serialize(upstreamOutputs, JsonOptions);
                    break;
                }

                default:
                    return Fail(node, $"Unsupported node type: {node.NodeType}");
            }

            node.State = QuestNodeState.Succeeded;
            node.Output = outputJson;
            return new OASISResult<QuestNode> { Result = node, Message = "Node executed successfully." };
        }
        catch (Exception ex)
        {
            return Fail(node, ex.Message);
        }
    }

    private static OASISResult<QuestNode> Fail(QuestNode node, string message)
    {
        node.State = QuestNodeState.Failed;
        node.Error = message;
        return new OASISResult<QuestNode> { IsError = true, Result = node, Message = message };
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<QuestTemplate>> CreateTemplateAsync(QuestTemplateCreateModel model, Guid avatarId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<QuestTemplate> { IsError = true, Message = activation.Message };

        var template = new QuestTemplate
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            Description = model.Description,
            AuthorAvatarId = avatarId,
            Parameters = model.Parameters,
            Version = model.Version,
            IsPublic = model.IsPublic,
            Tags = model.Tags
        };

        // Build template nodes from the create model
        var slotIds = new List<string>();
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            var nodeModel = model.Nodes[i];
            var slotId = $"slot_{i}";
            slotIds.Add(slotId);

            template.Nodes.Add(new QuestTemplateNode
            {
                Id = Guid.NewGuid(),
                TemplateId = template.Id,
                SlotId = slotId,
                NodeTemplateId = nodeModel.NodeTemplateId ?? Guid.Empty,
                ParamOverrides = nodeModel.Config,
                IsEntry = nodeModel.IsEntry,
                IsTerminal = nodeModel.IsTerminal
            });
        }

        foreach (var edgeModel in model.Edges)
        {
            if (edgeModel.SourceNodeId < 0 || edgeModel.SourceNodeId >= slotIds.Count ||
                edgeModel.TargetNodeId < 0 || edgeModel.TargetNodeId >= slotIds.Count)
            {
                return new OASISResult<QuestTemplate> { IsError = true, Message = "Edge index out of range." };
            }

            template.Edges.Add(new QuestTemplateEdge
            {
                Id = Guid.NewGuid(),
                TemplateId = template.Id,
                SourceSlotId = slotIds[edgeModel.SourceNodeId],
                TargetSlotId = slotIds[edgeModel.TargetNodeId],
                EdgeType = edgeModel.EdgeType
            });
        }

        return await _providerContext.CurrentProvider.SaveQuestTemplateAsync(template);
    }

    public async Task<OASISResult<QuestTemplate>> GetTemplateAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<QuestTemplate> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadQuestTemplateAsync(id);
    }

    public async Task<OASISResult<IEnumerable<QuestTemplate>>> ListTemplatesAsync(OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<QuestTemplate>> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadAllQuestTemplatesAsync();
    }

    public async Task<OASISResult<Quest>> InstantiateTemplateAsync(Guid templateId, Guid avatarId, Dictionary<string, string>? parameters = null, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<Quest> { IsError = true, Message = activation.Message };

        var templateResult = await _providerContext.CurrentProvider.LoadQuestTemplateAsync(templateId);
        if (templateResult.IsError || templateResult.Result == null)
            return new OASISResult<Quest> { IsError = true, Message = templateResult.Message };

        var template = templateResult.Result;

        // Load node templates referenced by this quest template
        var nodeTemplatesResult = await _providerContext.CurrentProvider.LoadAllQuestNodeTemplatesAsync();
        var nodeTemplates = (nodeTemplatesResult.Result ?? Enumerable.Empty<QuestNodeTemplate>())
            .ToDictionary(nt => nt.Id);

        var quest = new Quest
        {
            Id = Guid.NewGuid(),
            Name = template.Name,
            Description = template.Description,
            AvatarId = avatarId,
            TemplateId = templateId,
            Status = QuestStatus.Draft,
            CreatedDate = DateTime.UtcNow
        };

        // Map slotId -> new node Id
        var slotToNodeId = new Dictionary<string, Guid>();

        foreach (var templateNode in template.Nodes)
        {
            var nodeId = Guid.NewGuid();
            slotToNodeId[templateNode.SlotId] = nodeId;

            // Resolve config from node template with param overrides
            var config = templateNode.ParamOverrides;
            if (nodeTemplates.TryGetValue(templateNode.NodeTemplateId, out var nodeTemplate))
            {
                config = string.IsNullOrEmpty(templateNode.ParamOverrides) || templateNode.ParamOverrides == "{}"
                    ? nodeTemplate.DefaultConfig
                    : templateNode.ParamOverrides;
            }

            // Apply parameter substitutions
            if (parameters != null)
            {
                foreach (var (key, value) in parameters)
                    config = config.Replace($"{{{{{key}}}}}", value);
            }

            var nodeType = nodeTemplate?.NodeType ?? QuestNodeType.HolonGet;

            quest.Nodes.Add(new QuestNode
            {
                Id = nodeId,
                QuestId = quest.Id,
                NodeTemplateId = templateNode.NodeTemplateId,
                NodeType = nodeType,
                Name = nodeTemplate?.Name ?? templateNode.SlotId,
                Config = config,
                IsEntry = templateNode.IsEntry,
                IsTerminal = templateNode.IsTerminal,
                State = QuestNodeState.Pending
            });
        }

        foreach (var templateEdge in template.Edges)
        {
            if (!slotToNodeId.TryGetValue(templateEdge.SourceSlotId, out var sourceId) ||
                !slotToNodeId.TryGetValue(templateEdge.TargetSlotId, out var targetId))
                continue;

            quest.Edges.Add(new QuestEdge
            {
                Id = Guid.NewGuid(),
                QuestId = quest.Id,
                SourceNodeId = sourceId,
                TargetNodeId = targetId,
                EdgeType = templateEdge.EdgeType
            });
        }

        return await _providerContext.CurrentProvider.SaveQuestAsync(quest);
    }

    // ═══════════════════════════════════════════════════════════════════
    // NODE TEMPLATES
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<QuestNodeTemplate>> CreateNodeTemplateAsync(QuestNodeTemplateCreateModel model, Guid avatarId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<QuestNodeTemplate> { IsError = true, Message = activation.Message };

        var template = new QuestNodeTemplate
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            NodeType = model.NodeType,
            Description = model.Description,
            DefaultConfig = model.DefaultConfig,
            ConfigSchema = model.ConfigSchema,
            InputSchema = model.InputSchema,
            OutputSchema = model.OutputSchema,
            Version = model.Version,
            AuthorAvatarId = avatarId,
            IsPublic = model.IsPublic,
            Tags = model.Tags
        };

        return await _providerContext.CurrentProvider.SaveQuestNodeTemplateAsync(template);
    }

    public async Task<OASISResult<IEnumerable<QuestNodeTemplate>>> ListNodeTemplatesAsync(OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<QuestNodeTemplate>> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadAllQuestNodeTemplatesAsync();
    }
}

// ═══════════════════════════════════════════════════════════════════
// Internal config DTOs for node dispatch deserialization
// ═══════════════════════════════════════════════════════════════════

internal class IdConfig
{
    public Guid Id { get; set; }
}

internal class HolonUpdateNodeConfig
{
    public Guid HolonId { get; set; }
    public HolonUpdateModel Model { get; set; } = new();
}

internal class HolonInteractNodeConfig
{
    public Guid HolonId { get; set; }
    public HolonInteractionRequest Request { get; set; } = new();
}

internal class HolonPropagateNodeConfig
{
    public Guid HolonId { get; set; }
    public HolonPropagateRequest Request { get; set; } = new();
}

internal class HolonCloneNodeConfig
{
    public Guid HolonId { get; set; }
    public HolonCloneRequest Request { get; set; } = new();
}

internal class HolonMoveNodeConfig
{
    public Guid HolonId { get; set; }
    public Guid NewParentId { get; set; }
}

internal class NftTransferNodeConfig
{
    public Guid NftId { get; set; }
    public NftTransferRequest Request { get; set; } = new();
}

internal class NftBurnNodeConfig
{
    public Guid NftId { get; set; }
    public Guid WalletId { get; set; }
}

internal class WalletUpdateNodeConfig
{
    public Guid WalletId { get; set; }
    public WalletUpdateModel Model { get; set; } = new();
}

internal class WalletSetDefaultNodeConfig
{
    public Guid WalletId { get; set; }
}

internal class StarGenerateNodeConfig
{
    public Guid StarId { get; set; }
    public STARDappGenerationRequest Request { get; set; } = new();
}
