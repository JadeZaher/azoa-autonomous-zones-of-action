using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Managers;

public class HolonManager : IHolonManager
{
    private readonly IHolonStore _holonStore;

    // Opt-in AssetType registry (final-hardening-cutover F5). Optional so unit tests
    // that construct HolonManager with only a store keep compiling; DI always supplies
    // it. A null registry means validation is skipped entirely (free-string behaviour).
    // See Managers/AGENTS.md §holon-type-registry.
    private readonly IHolonTypeRegistryManager? _typeRegistry;
    private readonly INodeGovernanceGuard _nodeGovernance;

    public HolonManager(
        IHolonStore holonStore,
        IHolonTypeRegistryManager? typeRegistry = null,
        INodeGovernanceGuard? nodeGovernance = null)
    {
        _holonStore = holonStore;
        _typeRegistry = typeRegistry;
        _nodeGovernance = nodeGovernance ?? NodeGovernanceGuard.Unrestricted;
    }

    private static bool IsOwnedBy(IHolon holon, Guid avatarId) => holon.AvatarId == avatarId;

    // Cross-tenant read scope: a holon is readable iff the caller owns it OR it is
    // IsPublic. A null callerAvatarId fails closed (public-only). See
    // Controllers/AGENTS.md §cross-tenant-read-scope.
    private static bool CanRead(IHolon holon, Guid? callerAvatarId) =>
        callerAvatarId.HasValue && holon.AvatarId == callerAvatarId.Value || holon.IsPublic;

    public async Task<AZOAResult<IHolon>> GetAsync(Guid id, Guid? callerAvatarId = null, AZOARequest? request = null)
    {
        var result = await _holonStore.GetByIdAsync(id, default);
        // Do NOT confirm existence of a non-owned, non-public holon: return a
        // "not found"-style result so id-probing cannot enumerate other tenants.
        if (result.IsError || result.Result == null) return result;
        if (!CanRead(result.Result, callerAvatarId))
            return new AZOAResult<IHolon> { IsError = true, Message = "Holon not found." };
        return result;
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetAllAsync(Guid? callerAvatarId = null, AZOARequest? request = null)
    {
        var result = await _holonStore.QueryAsync(null, default);
        return ScopeReadable(result, callerAvatarId);
    }

    // Owner-or-public filter applied in-memory over a store result (mirrors
    // SearchManager). Preserves an error result verbatim.
    private static AZOAResult<IEnumerable<IHolon>> ScopeReadable(
        AZOAResult<IEnumerable<IHolon>> result, Guid? callerAvatarId)
    {
        if (result.IsError || result.Result == null) return result;
        return new AZOAResult<IEnumerable<IHolon>>
        {
            Result = result.Result.Where(h => CanRead(h, callerAvatarId)).ToList(),
            Message = result.Message
        };
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

        // F5 opt-in AssetType validation: constrains ONLY types registered in the
        // registry; unregistered types remain free strings.
        var governanceError = await EnsureGovernedAssetTypeAllowedAsync(holon.AssetType, "holon:create");
        if (governanceError != null)
            return new AZOAResult<IHolon> { IsError = true, Message = governanceError };

        var typeError = await ValidateAssetTypeAsync(holon.AssetType, holon.Metadata, request);
        if (typeError != null)
            return new AZOAResult<IHolon> { IsError = true, Message = typeError };

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

        // F5 opt-in AssetType validation against the post-merge type + metadata.
        var governanceError = await EnsureGovernedAssetTypeAllowedAsync(holon.AssetType, "holon:update");
        if (governanceError != null)
            return new AZOAResult<IHolon> { IsError = true, Message = governanceError };

        var typeError = await ValidateAssetTypeAsync(holon.AssetType, holon.Metadata, request);
        if (typeError != null)
            return new AZOAResult<IHolon> { IsError = true, Message = typeError };

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

    public async Task<AZOAResult<IEnumerable<IHolon>>> QueryAsync(HolonQueryRequest query, Guid? callerAvatarId = null, AZOARequest? request = null)
    {
        var result = await _holonStore.QueryAsync(query, default);
        return ScopeReadable(result, callerAvatarId);
    }

    public async Task<AZOAResult<IHolon>> InteractAsync(Guid id, HolonInteractionRequest request, Guid? avatarId = null, AZOARequest? providerRequest = null)
    {
        var existing = await _holonStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;
        // Fail-closed: a mutating call with no acting avatar is a wiring bug, not an admin escape hatch.
        if (!avatarId.HasValue || !IsOwnedBy(existing.Result, avatarId.Value))
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

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetChildrenAsync(Guid parentId, Guid? callerAvatarId = null, AZOARequest? request = null)
    {
        var query = new HolonQueryRequest { ParentHolonId = parentId };
        var result = await _holonStore.QueryAsync(query, default);
        return ScopeReadable(result, callerAvatarId);
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetPeersAsync(Guid id, Guid? callerAvatarId = null, AZOARequest? request = null)
    {
        var existing = await _holonStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return new AZOAResult<IEnumerable<IHolon>> { IsError = true, Message = existing.Message };
        // Own-or-public gate on the anchor holon: a non-owner may not enumerate a
        // private holon's peer graph (would leak peer ids by id-probing).
        if (!CanRead(existing.Result, callerAvatarId))
            return new AZOAResult<IEnumerable<IHolon>> { IsError = true, Message = "Holon not found." };

        var peerIds = existing.Result.PeerHolonIds;
        if (!peerIds.Any()) return new AZOAResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>(), Message = "No peers." };

        var all = await _holonStore.QueryAsync(null, default);
        var peers = all.Result?.Where(h => peerIds.Contains(h.Id) && CanRead(h, callerAvatarId)).ToList() ?? new List<IHolon>();

        return new AZOAResult<IEnumerable<IHolon>> { Result = peers, Message = $"Found {peers.Count} peers." };
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetAncestorsAsync(Guid id, Guid? callerAvatarId = null, AZOARequest? request = null)
    {
        var anchor = await _holonStore.GetByIdAsync(id, default);
        if (anchor.IsError || anchor.Result == null)
            return new AZOAResult<IEnumerable<IHolon>> { IsError = true, Message = anchor.Message ?? "Holon not found." };
        if (!CanRead(anchor.Result, callerAvatarId))
            return new AZOAResult<IEnumerable<IHolon>> { IsError = true, Message = "Holon not found." };

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

            // Only surface ancestors the caller may read (owner-or-public); a private
            // ancestor owned by another tenant is elided from the chain.
            if (CanRead(parentResult.Result, callerAvatarId))
                ancestors.Add(parentResult.Result);
            currentId = parentId.Value;
        }

        return new AZOAResult<IEnumerable<IHolon>> { Result = ancestors, Message = $"Found {ancestors.Count} ancestors." };
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetDescendantsAsync(Guid id, Guid? callerAvatarId = null, AZOARequest? request = null)
    {
        var anchor = await _holonStore.GetByIdAsync(id, default);
        if (anchor.IsError || anchor.Result == null)
            return new AZOAResult<IEnumerable<IHolon>> { IsError = true, Message = anchor.Message ?? "Holon not found." };
        if (!CanRead(anchor.Result, callerAvatarId))
            return new AZOAResult<IEnumerable<IHolon>> { IsError = true, Message = "Holon not found." };

        var descendants = await CollectDescendantsAsync(id);
        var readable = descendants.Where(h => CanRead(h, callerAvatarId)).ToList();
        return new AZOAResult<IEnumerable<IHolon>> { Result = readable, Message = $"Found {readable.Count} descendants." };
    }

    // Unfiltered subtree walk — the cycle guard (EnsureNotDescendantAsync) MUST see
    // every descendant regardless of owner, so it uses this raw collector, NOT the
    // scoped public GetDescendantsAsync.
    private async Task<List<IHolon>> CollectDescendantsAsync(Guid id)
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

        return descendants;
    }

    // ═══════════════════════════════════════════════════════════════════
    // HOLONIC FUNCTIONALITY
    // ═══════════════════════════════════════════════════════════════════

    public async Task<AZOAResult<int>> PropagateAsync(Guid id, HolonPropagateRequest request, Guid? avatarId = null, AZOARequest? providerRequest = null)
    {
        var rootResult = await _holonStore.GetByIdAsync(id, default);
        if (rootResult.IsError || rootResult.Result == null)
            return new AZOAResult<int> { IsError = true, Message = rootResult.Message ?? "Holon not found." };
        // Fail-closed: a mutating call with no acting avatar is a wiring bug, not an admin escape hatch.
        if (!avatarId.HasValue || !IsOwnedBy(rootResult.Result, avatarId.Value))
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

    public async Task<AZOAResult<HolonComposition>> ComposeAsync(Guid id, Guid? callerAvatarId = null, AZOARequest? request = null)
    {
        var rootResult = await _holonStore.GetByIdAsync(id, default);
        if (rootResult.IsError || rootResult.Result == null)
            return new AZOAResult<HolonComposition> { IsError = true, Message = "Holon not found." };
        // Own-or-public gate on the root: don't confirm a private holon's existence
        // or compose over another tenant's subtree.
        if (!CanRead(rootResult.Result, callerAvatarId))
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
                    // Traverse the full subtree structurally (depth stays correct) but
                    // only aggregate over holons the caller may read — a private
                    // cross-tenant descendant must not leak into the composition stats.
                    if (CanRead(child, callerAvatarId))
                        allDescendants.Add(child);
                    depthMap[child.Id] = currentDepth + 1;
                    queue.Enqueue(child.Id);
                }
            }
        }

        var allInSubtree = new List<IHolon>(allDescendants);
        if (!allInSubtree.Any(h => h.Id == id))
            allInSubtree.Add(root);

        var readableChildCount = (await _holonStore.QueryAsync(new HolonQueryRequest { ParentHolonId = id }, default))
            .Result?.Count(h => CanRead(h, callerAvatarId)) ?? 0;

        var composition = new HolonComposition
        {
            SourceHolonId = root.Id,
            SourceHolonName = root.Name,
            ChildCount = readableChildCount,
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
        // H-2: cross-avatar clone is the marketplace TEMPLATE mechanic — only the
        // owner, or anyone when the source is IsPublic, may clone. A non-owner
        // cloning a private holon is a cross-tenant IP-theft vector; fail closed as
        // "not found" so a private holon's existence is not confirmed by id probing.
        if (!IsOwnedBy(original, avatarId) && !original.IsPublic)
            return new AZOAResult<IHolon> { IsError = true, Message = "Holon not found." };

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
            // cross-avatar clone omits private_* metadata keys — see hardening review M2.
            Metadata = CloneMetadataWithoutPrivateKeys(original.Metadata, original.Id),
            PeerHolonIds = new List<Guid>(original.PeerHolonIds),
            IsActive = original.IsActive,
            // Clone provenance: link back to the source holon and its owner (the
            // cross-avatar template mechanic — caller becomes owner via AvatarId above).
            SourceHolonId = original.Id,
            OriginAvatarId = original.AvatarId
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
                        // cross-avatar clone omits private_* metadata keys — see hardening review M2.
                        Metadata = CloneMetadataWithoutPrivateKeys(child.Metadata, child.Id),
                        PeerHolonIds = new List<Guid>(),
                        IsActive = child.IsActive,
                        // Clone provenance: link back to the source child + its owner.
                        SourceHolonId = child.Id,
                        OriginAvatarId = child.AvatarId
                    };

                    idMap[child.Id] = childClone.Id;
                    await _holonStore.UpsertAsync(childClone, default);
                    queue.Enqueue(child.Id);
                }
            }
        }

        return new AZOAResult<IHolon> { Result = clone, Message = "Holon cloned." };
    }

    /// <summary>
    /// Copies metadata for a clone, unconditionally dropping any key starting with
    /// "private_" (case-insensitive) and stamping "cloned_from" provenance.
    /// Cross-avatar clone is an intentionally open template mechanic, so private_*
    /// keys must never leak into a non-owner's copy — see hardening review M2.
    /// </summary>
    private static Dictionary<string, string> CloneMetadataWithoutPrivateKeys(Dictionary<string, string> source, Guid sourceHolonId)
    {
        var copy = source
            .Where(kv => !kv.Key.StartsWith("private_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        copy["cloned_from"] = sourceHolonId.ToString();
        return copy;
    }

    public async Task<AZOAResult<bool>> MoveSubtreeAsync(Guid id, Guid newParentId, Guid? avatarId = null, AZOARequest? request = null)
    {
        // FR-6 / AC-6a: guard via shared helper (MoveSubtree precedent).
        var cycleError = await EnsureNotDescendantAsync(id, newParentId, request);
        if (cycleError != null) return new AZOAResult<bool> { IsError = true, Message = cycleError };

        var holonResult = await _holonStore.GetByIdAsync(id, default);
        if (holonResult.IsError || holonResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = "Holon not found." };
        // Fail-closed: a mutating call with no acting avatar is a wiring bug, not an admin escape hatch.
        if (!avatarId.HasValue || !IsOwnedBy(holonResult.Result, avatarId.Value))
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

        // Cycle detection must see the FULL subtree (unfiltered) — a private
        // descendant owned by another tenant still forms a cycle.
        var descendants = await CollectDescendantsAsync(holonId);
        if (descendants.Any(d => d.Id == proposedParentId))
            return "Cannot set a descendant holon as parent (cycle detected).";

        return null;
    }

    /// <summary>
    /// Opt-in AssetType registry hook (F5). Returns an error message when the holon's
    /// AssetType is registered + active AND its required metadata fields are not all
    /// present; returns null (allow) for an absent registry, an absent/unregistered/
    /// inactive type, or a satisfied constraint. See Managers/AGENTS.md §holon-type-registry.
    /// </summary>
    private async Task<string?> ValidateAssetTypeAsync(
        string? assetType, IReadOnlyDictionary<string, string>? metadata, AZOARequest? request)
    {
        if (_typeRegistry == null) return null; // registry not wired ⇒ free strings.

        var result = await _typeRegistry.ValidateAsync(assetType, metadata, request);
        return result.IsError ? result.Message : null;
    }

    private async Task<string?> EnsureGovernedAssetTypeAllowedAsync(string? assetType, string action)
    {
        var result = await _nodeGovernance.EnsureAssetTypeAllowedAsync(assetType, action);
        return result.IsError ? result.Message : null;
    }
}
