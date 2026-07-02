using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

public class HolonManager : IHolonManager
{
    private readonly IHolonStore _holonStore;

    public HolonManager(IHolonStore holonStore)
    {
        _holonStore = holonStore;
    }

    private static bool IsOwnedBy(IHolon holon, Guid avatarId) => holon.AvatarId == avatarId;

    public async Task<AZOAResult<IHolon>> GetAsync(Guid id, AZOARequest? request = null)
    {
        return await _holonStore.GetByIdAsync(id, default);
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetAllAsync(AZOARequest? request = null)
    {
        return await _holonStore.QueryAsync(null, default);
    }

    public async Task<AZOAResult<IHolon>> CreateAsync(HolonCreateModel model, Guid avatarId, AZOARequest? request = null)
    {
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

        // FR-6 / AC-6a: guard self-parent on create (descendant check is
        // vacuous for a brand-new holon, but self-parent is still a cycle).
        if (model.ParentHolonId.HasValue)
        {
            var cycleError = await EnsureNotDescendantAsync(holon.Id, model.ParentHolonId.Value, request);
            if (cycleError != null)
                return new AZOAResult<IHolon> { IsError = true, Message = cycleError };
        }

        return await _holonStore.UpsertAsync(holon, default);
    }

    public async Task<AZOAResult<IHolon>> UpdateAsync(Guid id, HolonUpdateModel model, Guid? avatarId = null, AZOARequest? request = null)
    {
        var existing = await _holonStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;
        if (avatarId.HasValue && !IsOwnedBy(existing.Result, avatarId.Value))
            return new AZOAResult<IHolon> { IsError = true, Message = "Holon is owned by a different avatar." };

        var holon = (Holon)existing.Result;
        if (model.Name != null) holon.Name = model.Name;
        if (model.Description != null) holon.Description = model.Description;

        // FR-6 / AC-6b: guard cycle on parent change.
        if (model.ParentHolonId.HasValue)
        {
            var cycleError = await EnsureNotDescendantAsync(id, model.ParentHolonId.Value, request);
            if (cycleError != null)
                return new AZOAResult<IHolon> { IsError = true, Message = cycleError };
        }
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

        return await _holonStore.UpsertAsync(holon, default);
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid? avatarId = null, AZOARequest? request = null)
    {
        var existing = await _holonStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = existing.Message ?? "Holon not found." };
        if (avatarId.HasValue && !IsOwnedBy(existing.Result, avatarId.Value))
            return new AZOAResult<bool> { IsError = true, Message = "Holon is owned by a different avatar." };

        return await _holonStore.DeleteAsync(id, default);
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> QueryAsync(HolonQueryRequest query, AZOARequest? request = null)
    {
        var result = await _holonStore.QueryAsync(query, default);
        return result;
    }

    public async Task<AZOAResult<IHolon>> InteractAsync(Guid id, HolonInteractionRequest request, Guid? avatarId = null, AZOARequest? providerRequest = null)
    {
        var existing = await _holonStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;
        if (avatarId.HasValue && !IsOwnedBy(existing.Result, avatarId.Value))
            return new AZOAResult<IHolon> { IsError = true, Message = "Holon is owned by a different avatar." };

        var holon = (Holon)existing.Result;

        // FR-6 / AC-6b: guard cycle on reparent via Interact.
        if (request.NewParentHolonId.HasValue)
        {
            var cycleError = await EnsureNotDescendantAsync(id, request.NewParentHolonId.Value, providerRequest);
            if (cycleError != null)
                return new AZOAResult<IHolon> { IsError = true, Message = cycleError };
            holon.ParentHolonId = request.NewParentHolonId;
        }

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
        return await _holonStore.UpsertAsync(holon, default);
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetChildrenAsync(Guid parentId, AZOARequest? request = null)
    {
        var query = new HolonQueryRequest { ParentHolonId = parentId };
        return await _holonStore.QueryAsync(query, default);
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetPeersAsync(Guid id, AZOARequest? request = null)
    {
        var existing = await _holonStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return new AZOAResult<IEnumerable<IHolon>> { IsError = true, Message = existing.Message };

        var peerIds = existing.Result.PeerHolonIds;
        if (!peerIds.Any()) return new AZOAResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>(), Message = "No peers." };

        var all = await _holonStore.QueryAsync(null, default);
        var peers = all.Result?.Where(h => peerIds.Contains(h.Id)).ToList() ?? new List<IHolon>();

        return new AZOAResult<IEnumerable<IHolon>> { Result = peers, Message = $"Found {peers.Count} peers." };
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetAncestorsAsync(Guid id, AZOARequest? request = null)
    {
        var ancestors = new List<IHolon>();
        var currentId = id;
        var visited = new HashSet<Guid> { id };

        while (true)
        {
            var result = await _holonStore.GetByIdAsync(currentId, default);
            if (result.IsError || result.Result == null) break;

            var parentId = result.Result.ParentHolonId;
            if (!parentId.HasValue) break;
            if (!visited.Add(parentId.Value)) break; // cycle guard

            var parentResult = await _holonStore.GetByIdAsync(parentId.Value, default);
            if (parentResult.IsError || parentResult.Result == null) break;

            ancestors.Add(parentResult.Result);
            currentId = parentId.Value;
        }

        return new AZOAResult<IEnumerable<IHolon>> { Result = ancestors, Message = $"Found {ancestors.Count} ancestors." };
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetDescendantsAsync(Guid id, AZOARequest? request = null)
    {
        var descendants = new List<IHolon>();
        var queue = new Queue<Guid>();
        queue.Enqueue(id);
        var visited = new HashSet<Guid> { id };

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var all = await _holonStore.QueryAsync(new HolonQueryRequest { ParentHolonId = current }, default);
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

        return new AZOAResult<IEnumerable<IHolon>> { Result = descendants, Message = $"Found {descendants.Count} descendants." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // HOLONIC FUNCTIONALITY
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<int>> PropagateAsync(Guid id, HolonPropagateRequest request, Guid? avatarId = null, AZOARequest? providerRequest = null)
    {
        var rootResult = await _holonStore.GetByIdAsync(id, default);
        if (rootResult.IsError || rootResult.Result == null)
            return new AZOAResult<int> { IsError = true, Message = rootResult.Message ?? "Holon not found." };
        if (avatarId.HasValue && !IsOwnedBy(rootResult.Result, avatarId.Value))
            return new AZOAResult<int> { IsError = true, Message = "Holon is owned by a different avatar." };

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
            var childrenResult = await _holonStore.QueryAsync(new HolonQueryRequest { ParentHolonId = id }, default);
            foreach (var child in childrenResult.Result ?? Enumerable.Empty<IHolon>())
            {
                if (visited.Add(child.Id))
                    queue.Enqueue(child.Id);
            }
        }

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            var holonResult = await _holonStore.GetByIdAsync(currentId, default);
            if (holonResult.IsError || holonResult.Result == null) continue;

            var holon = (Holon)holonResult.Result;

            if (request.Property.Equals("IsActive", StringComparison.OrdinalIgnoreCase))
                holon.IsActive = request.Value;
            else
                holon.Metadata[$"propagated_{request.Property}"] = request.Value.ToString().ToLowerInvariant();

            holon.ModifiedDate = DateTime.UtcNow;
            await _holonStore.UpsertAsync(holon, default);
            count++;

            var childrenResult = await _holonStore.QueryAsync(new HolonQueryRequest { ParentHolonId = currentId }, default);
            foreach (var child in childrenResult.Result ?? Enumerable.Empty<IHolon>())
            {
                if (visited.Add(child.Id))
                    queue.Enqueue(child.Id);
            }
        }

        return new AZOAResult<int> { Result = count, Message = $"Propagated to {count} holons." };
    }

    public async Task<AZOAResult<HolonComposition>> ComposeAsync(Guid id, AZOARequest? request = null)
    {
        var rootResult = await _holonStore.GetByIdAsync(id, default);
        if (rootResult.IsError || rootResult.Result == null)
            return new AZOAResult<HolonComposition> { IsError = true, Message = "Holon not found." };

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

            var childrenResult = await _holonStore.QueryAsync(new HolonQueryRequest { ParentHolonId = currentId }, default);
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
            ChildCount = (await _holonStore.QueryAsync(new HolonQueryRequest { ParentHolonId = id }, default)).Result?.Count() ?? 0,
            TotalDescendantCount = allDescendants.Count,
            Depth = maxDepth,
            AssetTypes = allInSubtree.Where(h => !string.IsNullOrEmpty(h.AssetType)).Select(h => h.AssetType!).Distinct().ToList(),
            ChainIds = allInSubtree.Where(h => !string.IsNullOrEmpty(h.ChainId)).Select(h => h.ChainId!).Distinct().ToList(),
            MetadataKeyFrequency = allInSubtree.SelectMany(h => h.Metadata.Keys).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count()),
            AllActive = allInSubtree.All(h => h.IsActive),
            EarliestCreated = allInSubtree.Min(h => (DateTime?)h.CreatedDate),
            LatestModified = allInSubtree.Max(h => h.ModifiedDate)
        };

        return new AZOAResult<HolonComposition> { Result = composition, Message = "Composition computed." };
    }

    public async Task<AZOAResult<IHolon>> CloneAsync(Guid id, HolonCloneRequest request, Guid avatarId, AZOARequest? providerRequest = null)
    {
        var originalResult = await _holonStore.GetByIdAsync(id, default);
        if (originalResult.IsError || originalResult.Result == null)
            return new AZOAResult<IHolon> { IsError = true, Message = "Holon not found." };

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
        await _holonStore.UpsertAsync(clone, default);

        if (request.IncludeSubtree)
        {
            // BFS clone all descendants, remapping parent IDs
            var queue = new Queue<Guid>();
            queue.Enqueue(original.Id);
            var visited = new HashSet<Guid> { original.Id };

            while (queue.Count > 0)
            {
                var currentOriginalId = queue.Dequeue();
                var childrenResult = await _holonStore.QueryAsync(new HolonQueryRequest { ParentHolonId = currentOriginalId }, default);
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
                    await _holonStore.UpsertAsync(childClone, default);
                    queue.Enqueue(child.Id);
                }
            }
        }

        return new AZOAResult<IHolon> { Result = clone, Message = "Holon cloned." };
    }

    public async Task<AZOAResult<bool>> MoveSubtreeAsync(Guid id, Guid newParentId, Guid? avatarId = null, AZOARequest? request = null)
    {
        // FR-6 / AC-6a: guard via shared helper (MoveSubtree precedent).
        var cycleError = await EnsureNotDescendantAsync(id, newParentId, request);
        if (cycleError != null) return new AZOAResult<bool> { IsError = true, Message = cycleError };

        var holonResult = await _holonStore.GetByIdAsync(id, default);
        if (holonResult.IsError || holonResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = "Holon not found." };
        if (avatarId.HasValue && !IsOwnedBy(holonResult.Result, avatarId.Value))
            return new AZOAResult<bool> { IsError = true, Message = "Holon is owned by a different avatar." };

        var holon = (Holon)holonResult.Result;
        holon.ParentHolonId = newParentId;
        holon.ModifiedDate = DateTime.UtcNow;

        var saveResult = await _holonStore.UpsertAsync(holon, default);
        if (saveResult.IsError)
            return new AZOAResult<bool> { IsError = true, Message = saveResult.Message };

        return new AZOAResult<bool> { Result = true, Message = "Subtree moved." };
    }

    /// <summary>
    /// Returns an error message if <paramref name="proposedParentId"/> is a
    /// descendant of <paramref name="holonId"/> (which would create a cycle),
    /// or if the descendants could not be fetched. Returns null when safe.
    /// See Managers/AGENTS.md §holon-parent-cycle.
    /// </summary>
    private async Task<string?> EnsureNotDescendantAsync(
        Guid holonId, Guid proposedParentId, AZOARequest? request = null)
    {
        if (holonId == proposedParentId)
            return "Cannot set a holon as its own parent.";

        var descendantsResult = await GetDescendantsAsync(holonId, request);
        if (descendantsResult.IsError)
            return $"Could not verify parent cycle: {descendantsResult.Message}";

        if (descendantsResult.Result?.Any(d => d.Id == proposedParentId) == true)
            return "Cannot set a descendant holon as parent (cycle detected).";

        return null;
    }
}
