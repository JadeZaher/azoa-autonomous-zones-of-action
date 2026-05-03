using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Managers;

public class HolonManager : IHolonManager
{
    private readonly ProviderContext _providerContext;

    public HolonManager(ProviderContext providerContext)
    {
        _providerContext = providerContext;
    }

    public async Task<OASISResult<IHolon>> GetAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IHolon> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadHolonAsync(id);
    }

    public async Task<OASISResult<IEnumerable<IHolon>>> GetAllAsync(OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IHolon>> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadAllHolonsAsync();
    }

    public async Task<OASISResult<IHolon>> CreateAsync(HolonCreateModel model, Guid avatarId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IHolon> { IsError = true, Message = activation.Message };

        var holon = new Holon
        {
            Name = model.Name,
            Description = model.Description,
            ParentHolonId = model.ParentHolonId,
            AvatarId = avatarId,
            ProviderName = model.ProviderName,
            ChainId = model.ChainId,
            AssetType = model.AssetType,
            TokenId = model.TokenId,
            Metadata = model.Metadata,
            PeerHolonIds = model.PeerHolonIds
        };

        return await _providerContext.CurrentProvider.SaveHolonAsync(holon);
    }

    public async Task<OASISResult<IHolon>> UpdateAsync(Guid id, HolonUpdateModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IHolon> { IsError = true, Message = activation.Message };

        var existing = await _providerContext.CurrentProvider.LoadHolonAsync(id);
        if (existing.IsError || existing.Result == null) return existing;

        var holon = (Holon)existing.Result;
        if (model.Name != null) holon.Name = model.Name;
        if (model.Description != null) holon.Description = model.Description;
        if (model.ParentHolonId.HasValue) holon.ParentHolonId = model.ParentHolonId;
        if (model.ProviderName != null) holon.ProviderName = model.ProviderName;
        if (model.ChainId != null) holon.ChainId = model.ChainId;
        if (model.AssetType != null) holon.AssetType = model.AssetType;
        if (model.TokenId != null) holon.TokenId = model.TokenId;
        if (model.Metadata != null)
        {
            foreach (var kv in model.Metadata)
                holon.Metadata[kv.Key] = kv.Value;
        }
        if (model.PeerHolonIds != null) holon.PeerHolonIds = model.PeerHolonIds;
        if (model.IsActive.HasValue) holon.IsActive = model.IsActive.Value;
        holon.ModifiedDate = DateTime.UtcNow;

        return await _providerContext.CurrentProvider.SaveHolonAsync(holon);
    }

    public async Task<OASISResult<bool>> DeleteAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.DeleteHolonAsync(id);
    }

    public async Task<OASISResult<IEnumerable<IHolon>>> QueryAsync(HolonQueryRequest query, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IHolon>> { IsError = true, Message = activation.Message };

        var result = await _providerContext.CurrentProvider.LoadAllHolonsAsync(query);
        return result;
    }

    public async Task<OASISResult<IHolon>> InteractAsync(Guid id, HolonInteractionRequest request, OASISRequest? providerRequest = null)
    {
        var activation = _providerContext.Activate(providerRequest);
        if (activation.IsError) return new OASISResult<IHolon> { IsError = true, Message = activation.Message };

        var existing = await _providerContext.CurrentProvider.LoadHolonAsync(id);
        if (existing.IsError || existing.Result == null) return existing;

        var holon = (Holon)existing.Result;

        if (request.NewParentHolonId.HasValue)
            holon.ParentHolonId = request.NewParentHolonId;

        foreach (var peerId in request.AddPeerHolonIds)
        {
            if (!holon.PeerHolonIds.Contains(peerId))
                holon.PeerHolonIds.Add(peerId);
        }

        foreach (var peerId in request.RemovePeerHolonIds)
        {
            holon.PeerHolonIds.Remove(peerId);
        }

        if (request.SetMetadata != null)
        {
            foreach (var kv in request.SetMetadata)
                holon.Metadata[kv.Key] = kv.Value;
        }

        if (request.RemoveMetadataKeys != null)
        {
            foreach (var key in request.RemoveMetadataKeys)
                holon.Metadata.Remove(key);
        }

        holon.ModifiedDate = DateTime.UtcNow;
        return await _providerContext.CurrentProvider.SaveHolonAsync(holon);
    }

    public async Task<OASISResult<IEnumerable<IHolon>>> GetChildrenAsync(Guid parentId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IHolon>> { IsError = true, Message = activation.Message };

        var query = new HolonQueryRequest { ParentHolonId = parentId };
        return await _providerContext.CurrentProvider.LoadAllHolonsAsync(query);
    }

    public async Task<OASISResult<IEnumerable<IHolon>>> GetPeersAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IHolon>> { IsError = true, Message = activation.Message };

        var existing = await _providerContext.CurrentProvider.LoadHolonAsync(id);
        if (existing.IsError || existing.Result == null) return new OASISResult<IEnumerable<IHolon>> { IsError = true, Message = existing.Message };

        var peerIds = existing.Result.PeerHolonIds;
        if (!peerIds.Any()) return new OASISResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>(), Message = "No peers." };

        var all = await _providerContext.CurrentProvider.LoadAllHolonsAsync();
        var peers = all.Result?.Where(h => peerIds.Contains(h.Id)).ToList() ?? new List<IHolon>();

        return new OASISResult<IEnumerable<IHolon>> { Result = peers, Message = $"Found {peers.Count} peers." };
    }

    public async Task<OASISResult<IEnumerable<IHolon>>> GetAncestorsAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IHolon>> { IsError = true, Message = activation.Message };

        var ancestors = new List<IHolon>();
        var currentId = id;
        var visited = new HashSet<Guid> { id };

        while (true)
        {
            var result = await _providerContext.CurrentProvider.LoadHolonAsync(currentId);
            if (result.IsError || result.Result == null) break;

            var parentId = result.Result.ParentHolonId;
            if (!parentId.HasValue) break;
            if (!visited.Add(parentId.Value)) break; // cycle guard

            var parentResult = await _providerContext.CurrentProvider.LoadHolonAsync(parentId.Value);
            if (parentResult.IsError || parentResult.Result == null) break;

            ancestors.Add(parentResult.Result);
            currentId = parentId.Value;
        }

        return new OASISResult<IEnumerable<IHolon>> { Result = ancestors, Message = $"Found {ancestors.Count} ancestors." };
    }

    public async Task<OASISResult<IEnumerable<IHolon>>> GetDescendantsAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IHolon>> { IsError = true, Message = activation.Message };

        var descendants = new List<IHolon>();
        var queue = new Queue<Guid>();
        queue.Enqueue(id);
        var visited = new HashSet<Guid> { id };

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var all = await _providerContext.CurrentProvider.LoadAllHolonsAsync(new HolonQueryRequest { ParentHolonId = current });
            var children = all.Result?.ToList() ?? new List<IHolon>();

            foreach (var child in children)
            {
                if (visited.Add(child.Id))
                {
                    descendants.Add(child);
                    queue.Enqueue(child.Id);
                }
            }
        }

        return new OASISResult<IEnumerable<IHolon>> { Result = descendants, Message = $"Found {descendants.Count} descendants." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // HOLONIC FUNCTIONALITY
    // ═══════════════════════════════════════════════════════════════════

    public async Task<OASISResult<int>> PropagateAsync(Guid id, HolonPropagateRequest request, OASISRequest? providerRequest = null)
    {
        var activation = _providerContext.Activate(providerRequest);
        if (activation.IsError) return new OASISResult<int> { IsError = true, Message = activation.Message };

        var provider = _providerContext.CurrentProvider;
        var count = 0;
        var queue = new Queue<Guid>();
        var visited = new HashSet<Guid>();

        if (request.IncludeSelf)
        {
            queue.Enqueue(id);
            visited.Add(id);
        }
        else
        {
            // Start with children only
            var childrenResult = await provider.LoadAllHolonsAsync(new HolonQueryRequest { ParentHolonId = id });
            foreach (var child in childrenResult.Result ?? Enumerable.Empty<IHolon>())
            {
                if (visited.Add(child.Id))
                    queue.Enqueue(child.Id);
            }
        }

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            var holonResult = await provider.LoadHolonAsync(currentId);
            if (holonResult.IsError || holonResult.Result == null) continue;

            var holon = (Holon)holonResult.Result;

            if (request.Property.Equals("IsActive", StringComparison.OrdinalIgnoreCase))
                holon.IsActive = request.Value;
            else
                holon.Metadata[$"propagated_{request.Property}"] = request.Value.ToString().ToLowerInvariant();

            holon.ModifiedDate = DateTime.UtcNow;
            await provider.SaveHolonAsync(holon);
            count++;

            var childrenResult = await provider.LoadAllHolonsAsync(new HolonQueryRequest { ParentHolonId = currentId });
            foreach (var child in childrenResult.Result ?? Enumerable.Empty<IHolon>())
            {
                if (visited.Add(child.Id))
                    queue.Enqueue(child.Id);
            }
        }

        return new OASISResult<int> { Result = count, Message = $"Propagated to {count} holons." };
    }

    public async Task<OASISResult<HolonComposition>> ComposeAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<HolonComposition> { IsError = true, Message = activation.Message };

        var provider = _providerContext.CurrentProvider;
        var rootResult = await provider.LoadHolonAsync(id);
        if (rootResult.IsError || rootResult.Result == null)
            return new OASISResult<HolonComposition> { IsError = true, Message = "Holon not found." };

        var root = rootResult.Result;
        var allDescendants = new List<IHolon>();
        var queue = new Queue<Guid>();
        queue.Enqueue(id);
        var visited = new HashSet<Guid> { id };
        int maxDepth = 0;
        var depthMap = new Dictionary<Guid, int> { [id] = 0 };

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            var currentDepth = depthMap[currentId];
            maxDepth = Math.Max(maxDepth, currentDepth);

            var childrenResult = await provider.LoadAllHolonsAsync(new HolonQueryRequest { ParentHolonId = currentId });
            var children = childrenResult.Result?.ToList() ?? new List<IHolon>();

            foreach (var child in children)
            {
                if (visited.Add(child.Id))
                {
                    allDescendants.Add(child);
                    depthMap[child.Id] = currentDepth + 1;
                    queue.Enqueue(child.Id);
                }
            }
        }

        var allInSubtree = new List<IHolon>(allDescendants);
        if (!allInSubtree.Any(h => h.Id == id))
            allInSubtree.Add(root);

        var composition = new HolonComposition
        {
            SourceHolonId = root.Id,
            SourceHolonName = root.Name,
            ChildCount = (await provider.LoadAllHolonsAsync(new HolonQueryRequest { ParentHolonId = id })).Result?.Count() ?? 0,
            TotalDescendantCount = allDescendants.Count,
            Depth = maxDepth,
            AssetTypes = allInSubtree.Where(h => !string.IsNullOrEmpty(h.AssetType)).Select(h => h.AssetType!).Distinct().ToList(),
            ChainIds = allInSubtree.Where(h => !string.IsNullOrEmpty(h.ChainId)).Select(h => h.ChainId!).Distinct().ToList(),
            MetadataKeyFrequency = allInSubtree.SelectMany(h => h.Metadata.Keys).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count()),
            AllActive = allInSubtree.All(h => h.IsActive),
            EarliestCreated = allInSubtree.Min(h => (DateTime?)h.CreatedDate),
            LatestModified = allInSubtree.Max(h => h.ModifiedDate)
        };

        return new OASISResult<HolonComposition> { Result = composition, Message = "Composition computed." };
    }

    public async Task<OASISResult<IHolon>> CloneAsync(Guid id, HolonCloneRequest request, Guid avatarId, OASISRequest? providerRequest = null)
    {
        var activation = _providerContext.Activate(providerRequest);
        if (activation.IsError) return new OASISResult<IHolon> { IsError = true, Message = activation.Message };

        var provider = _providerContext.CurrentProvider;
        var originalResult = await provider.LoadHolonAsync(id);
        if (originalResult.IsError || originalResult.Result == null)
            return new OASISResult<IHolon> { IsError = true, Message = "Holon not found." };

        var original = (Holon)originalResult.Result;
        var idMap = new Dictionary<Guid, Guid>();

        // Clone the root
        var clone = new Holon
        {
            Id = Guid.NewGuid(),
            Name = request.Name ?? $"{original.Name} (Copy)",
            Description = original.Description,
            ParentHolonId = request.NewParentId,
            AvatarId = avatarId,
            ProviderName = original.ProviderName,
            ChainId = original.ChainId,
            AssetType = original.AssetType,
            TokenId = null, // cloned holon is not the same on-chain asset
            Metadata = new Dictionary<string, string>(original.Metadata) { ["cloned_from"] = original.Id.ToString() },
            PeerHolonIds = new List<Guid>(original.PeerHolonIds),
            IsActive = original.IsActive
        };

        idMap[original.Id] = clone.Id;
        await provider.SaveHolonAsync(clone);

        if (request.IncludeSubtree)
        {
            // BFS clone all descendants, remapping parent IDs
            var queue = new Queue<Guid>();
            queue.Enqueue(original.Id);
            var visited = new HashSet<Guid> { original.Id };

            while (queue.Count > 0)
            {
                var currentOriginalId = queue.Dequeue();
                var childrenResult = await provider.LoadAllHolonsAsync(new HolonQueryRequest { ParentHolonId = currentOriginalId });
                var children = childrenResult.Result?.ToList() ?? new List<IHolon>();

                foreach (var child in children)
                {
                    if (!visited.Add(child.Id)) continue;

                    var childClone = new Holon
                    {
                        Id = Guid.NewGuid(),
                        Name = child.Name,
                        Description = child.Description,
                        ParentHolonId = idMap[currentOriginalId],
                        AvatarId = avatarId,
                        ProviderName = child.ProviderName,
                        ChainId = child.ChainId,
                        AssetType = child.AssetType,
                        TokenId = null,
                        Metadata = new Dictionary<string, string>(child.Metadata) { ["cloned_from"] = child.Id.ToString() },
                        PeerHolonIds = new List<Guid>(),
                        IsActive = child.IsActive
                    };

                    idMap[child.Id] = childClone.Id;
                    await provider.SaveHolonAsync(childClone);
                    queue.Enqueue(child.Id);
                }
            }
        }

        return new OASISResult<IHolon> { Result = clone, Message = "Holon cloned." };
    }

    public async Task<OASISResult<bool>> MoveSubtreeAsync(Guid id, Guid newParentId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var provider = _providerContext.CurrentProvider;

        // Prevent moving a holon under its own descendant (would create a cycle)
        var descendantsResult = await GetDescendantsAsync(id, request);
        if (descendantsResult.Result?.Any(d => d.Id == newParentId) == true)
            return new OASISResult<bool> { IsError = true, Message = "Cannot move a holon under its own descendant." };

        var holonResult = await provider.LoadHolonAsync(id);
        if (holonResult.IsError || holonResult.Result == null)
            return new OASISResult<bool> { IsError = true, Message = "Holon not found." };

        var holon = (Holon)holonResult.Result;
        holon.ParentHolonId = newParentId;
        holon.ModifiedDate = DateTime.UtcNow;

        var saveResult = await provider.SaveHolonAsync(holon);
        if (saveResult.IsError)
            return new OASISResult<bool> { IsError = true, Message = saveResult.Message };

        return new OASISResult<bool> { Result = true, Message = "Subtree moved." };
    }
}
