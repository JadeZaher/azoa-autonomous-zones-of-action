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

    // The consumer-governance vote of record (W9.2). Optional for the same reason the
    // registry is — unit tests construct this manager with a store alone — but its
    // ABSENCE is a refusal, not a skip: see ProposalFreezeStateAsync.
    private readonly IPersonOperationClaimStore? _voteOfRecord;

    public HolonManager(
        IHolonStore holonStore,
        IHolonTypeRegistryManager? typeRegistry = null,
        INodeGovernanceGuard? nodeGovernance = null,
        IPersonOperationClaimStore? voteOfRecord = null)
    {
        _holonStore = holonStore;
        _typeRegistry = typeRegistry;
        _nodeGovernance = nodeGovernance ?? NodeGovernanceGuard.Unrestricted;
        _voteOfRecord = voteOfRecord;
    }

    private static bool IsOwnedBy(IHolon holon, Guid userId) => holon.UserId == userId;

    // Cross-tenant read scope: a holon is readable iff the caller owns it OR it is
    // IsPublic. A null callerUserId fails closed (public-only). See
    // Controllers/AGENTS.md §cross-tenant-read-scope.
    private static bool CanRead(IHolon holon, Guid? callerUserId) =>
        callerUserId.HasValue && holon.UserId == callerUserId.Value || holon.IsPublic;

    // Publication allow-list. IsPublic is what CanRead above reads, so it makes a
    // holon readable by EVERY caller on the node; this repo has leaked cross-tenant
    // through generic read surfaces three times, so the flag is deliberately NOT a
    // free consumer toggle. A type earns a place here only when the artefact is
    // meaningless unless published: a governance proposal is the notice its
    // electorate votes on, and an unpublished one cannot be voted on at all (D20).
    //
    // GovernanceVote is deliberately ABSENT. A public vote holon carries Holon.UserId
    // and would reconstitute the person-to-choice correlation the whole D20 design
    // exists to prevent. Since D39 that holon is only the voter's RECEIPT: the vote of
    // record is the person_operation_claim row, and the tally counts that ledger and
    // reads no holon ballot at all. See Services/Governance/AGENTS.md
    // #consumer-governance.
    internal static readonly IReadOnlySet<string> PublishableAssetTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ConsumerGovernanceHolonTypes.ProposalAssetType,
        };

    // Refusal, never a silent drop: a caller that asked for a public holon and got a
    // private one back would believe its audience can read it.
    private static string? PublicationError(IHolon holon) =>
        holon.IsPublic && (holon.AssetType is null || !PublishableAssetTypes.Contains(holon.AssetType))
            ? $"Holon AssetType '{holon.AssetType ?? "(none)"}' may not be published."
            : null;

    public async Task<AZOAResult<IHolon>> GetAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null)
    {
        var result = await _holonStore.GetByIdAsync(id, default);
        // Do NOT confirm existence of a non-owned, non-public holon: return a
        // "not found"-style result so id-probing cannot enumerate other tenants.
        if (result.IsError || result.Result == null) return result;
        if (!CanRead(result.Result, callerUserId))
            return new AZOAResult<IHolon> { IsError = true, Message = "Holon not found." };
        // A governance proposal whose recorded electorate names the whole node is not a
        // notice, it is a franchise nobody granted. This is the read BOTH governance
        // nodes perform, so refusing here is what stops a row written before the rule
        // from being voted on or tallied. Same sentence as the CanRead refusal: an
        // existence oracle is not worth the diagnostic.
        if (ConsumerGovernanceHolonTypes.ProposalElectorateRejection(
                result.Result.AssetType, result.Result.Metadata) != null)
            return new AZOAResult<IHolon> { IsError = true, Message = "Holon not found." };
        return result;
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetAllAsync(Guid? callerUserId = null, AZOARequest? request = null)
    {
        var result = await _holonStore.QueryAsync(null, default);
        return ScopeReadable(result, callerUserId);
    }

    // Owner-or-public filter applied in-memory over a store result (mirrors
    // SearchManager). Preserves an error result verbatim.
    private static AZOAResult<IEnumerable<IHolon>> ScopeReadable(
        AZOAResult<IEnumerable<IHolon>> result, Guid? callerUserId)
    {
        if (result.IsError || result.Result == null) return result;
        return new AZOAResult<IEnumerable<IHolon>>
        {
            Result = result.Result
                .Where(h => CanRead(h, callerUserId)
                    && ConsumerGovernanceHolonTypes.ProposalElectorateRejection(h.AssetType, h.Metadata) == null)
                .ToList(),
            Message = result.Message
        };
    }

    public async Task<AZOAResult<IHolon>> CreateAsync(HolonCreateModel model, Guid userId, AZOARequest? request = null)
    {
        var holon = new Holon
        {
            Name = model.Name,
            Description = model.Description,
            ParentHolonId = model.ParentHolonId,
            UserId = userId,
            ProviderName = model.ProviderName,
            ChainId = model.ChainId,
            AssetType = model.AssetType,
            TokenId = model.TokenId,
            Metadata = model.Metadata,
            PeerHolonIds = model.PeerHolonIds,
            IsPublic = model.IsPublic
        };

        var publicationError = PublicationError(holon);
        if (publicationError != null)
            return new AZOAResult<IHolon> { IsError = true, Message = publicationError };

        // W9.2. The electorate is minted from the ACTING tenant, and a plain user JWT
        // carries none — so GovernanceProposalCreate would hand an ordinary member the
        // whole node, and POST /api/holon would hand it to anyone who can type "node".
        // Both go through here.
        var electorateError = ConsumerGovernanceHolonTypes.ProposalElectorateRejection(holon.AssetType, holon.Metadata);
        if (electorateError != null)
            return new AZOAResult<IHolon> { IsError = true, Message = electorateError };

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

    public async Task<AZOAResult<IHolon>> UpdateAsync(Guid id, HolonUpdateModel model, Guid? userId = null, AZOARequest? request = null)
    {
        var existing = await _holonStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;
        if (userId.HasValue && !IsOwnedBy(existing.Result, userId.Value))
            return new AZOAResult<IHolon> { IsError = true, Message = "Holon is owned by a different user." };

        // W9.2 - the proposal freeze. Runs on the PRE-MERGE state, so an author cannot
        // dodge it by rewriting the uniqueness domain in the same request that moves the
        // question. See Services/Governance/AGENTS.md #proposal-freeze.
        var freezeError = await FrozenProposalRewriteErrorAsync(
            existing.Result, model.Metadata, model.IsPublic, model.AssetType);
        if (freezeError != null)
            return new AZOAResult<IHolon> { IsError = true, Message = freezeError };

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
        if (model.IsPublic.HasValue) holon.IsPublic = model.IsPublic.Value;
        holon.ModifiedDate = DateTime.UtcNow;

        // Checked against the POST-MERGE state: publishing as a proposal and then
        // renaming the AssetType would otherwise leave a published holon of an
        // unpublishable type.
        var publicationError = PublicationError(holon);
        if (publicationError != null)
            return new AZOAResult<IHolon> { IsError = true, Message = publicationError };

        // Post-merge for the same reason: an update that sets electorateTenant to the
        // node value is refused here even though the recorded value was fine.
        var electorateError = ConsumerGovernanceHolonTypes.ProposalElectorateRejection(holon.AssetType, holon.Metadata);
        if (electorateError != null)
            return new AZOAResult<IHolon> { IsError = true, Message = electorateError };

        // F5 opt-in AssetType validation against the post-merge type + metadata.
        var governanceError = await EnsureGovernedAssetTypeAllowedAsync(holon.AssetType, "holon:update");
        if (governanceError != null)
            return new AZOAResult<IHolon> { IsError = true, Message = governanceError };

        var typeError = await ValidateAssetTypeAsync(holon.AssetType, holon.Metadata, request);
        if (typeError != null)
            return new AZOAResult<IHolon> { IsError = true, Message = typeError };

        return await _holonStore.UpsertAsync(holon, default);
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid? userId = null, AZOARequest? request = null)
    {
        var existing = await _holonStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = existing.Message ?? "Holon not found." };
        if (userId.HasValue && !IsOwnedBy(existing.Result, userId.Value))
            return new AZOAResult<bool> { IsError = true, Message = "Holon is owned by a different user." };

        // Deleting a decided proposal destroys the question while the votes of record
        // live on, which leaves a tally nobody can run rather than a result somebody can
        // dispute. Same freeze, same ledger.
        var freezeError = await FrozenProposalErrorAsync(existing.Result, "a vote has already been recorded on it");
        if (freezeError != null)
            return new AZOAResult<bool> { IsError = true, Message = freezeError };

        return await _holonStore.DeleteAsync(id, default);
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> QueryAsync(HolonQueryRequest query, Guid? callerUserId = null, AZOARequest? request = null)
    {
        var result = await _holonStore.QueryAsync(query, default);
        return ScopeReadable(result, callerUserId);
    }

    public async Task<AZOAResult<IHolon>> InteractAsync(Guid id, HolonInteractionRequest request, Guid? userId = null, AZOARequest? providerRequest = null)
    {
        var existing = await _holonStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;
        // Fail-closed: a mutating call with no acting user is a wiring bug, not an admin escape hatch.
        if (!userId.HasValue || !IsOwnedBy(existing.Result, userId.Value))
            return new AZOAResult<IHolon> { IsError = true, Message = "Holon is owned by a different user." };

        // W9.2. InteractAsync is the manager's SECOND metadata-mutation seam: it sets
        // and REMOVES arbitrary keys on any holon its caller owns, which is every frozen
        // field on a proposal. A freeze enforced only on UpdateAsync would be a freeze
        // with a documented way around it.
        var interactionFreezeError = await FrozenProposalRewriteErrorAsync(
            existing.Result,
            request.SetMetadata,
            incomingIsPublic: null,
            incomingAssetType: null,
            request.RemoveMetadataKeys);
        if (interactionFreezeError != null)
            return new AZOAResult<IHolon> { IsError = true, Message = interactionFreezeError };

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

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetChildrenAsync(Guid parentId, Guid? callerUserId = null, AZOARequest? request = null)
    {
        var query = new HolonQueryRequest { ParentHolonId = parentId };
        var result = await _holonStore.QueryAsync(query, default);
        return ScopeReadable(result, callerUserId);
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetPeersAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null)
    {
        var existing = await _holonStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return new AZOAResult<IEnumerable<IHolon>> { IsError = true, Message = existing.Message };
        // Own-or-public gate on the anchor holon: a non-owner may not enumerate a
        // private holon's peer graph (would leak peer ids by id-probing).
        if (!CanRead(existing.Result, callerUserId))
            return new AZOAResult<IEnumerable<IHolon>> { IsError = true, Message = "Holon not found." };

        var peerIds = existing.Result.PeerHolonIds;
        if (!peerIds.Any()) return new AZOAResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>(), Message = "No peers." };

        var all = await _holonStore.QueryAsync(null, default);
        var peers = all.Result?.Where(h => peerIds.Contains(h.Id) && CanRead(h, callerUserId)).ToList() ?? new List<IHolon>();

        return new AZOAResult<IEnumerable<IHolon>> { Result = peers, Message = $"Found {peers.Count} peers." };
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetAncestorsAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null)
    {
        var anchor = await _holonStore.GetByIdAsync(id, default);
        if (anchor.IsError || anchor.Result == null)
            return new AZOAResult<IEnumerable<IHolon>> { IsError = true, Message = anchor.Message ?? "Holon not found." };
        if (!CanRead(anchor.Result, callerUserId))
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
            if (CanRead(parentResult.Result, callerUserId))
                ancestors.Add(parentResult.Result);
            currentId = parentId.Value;
        }

        return new AZOAResult<IEnumerable<IHolon>> { Result = ancestors, Message = $"Found {ancestors.Count} ancestors." };
    }

    public async Task<AZOAResult<IEnumerable<IHolon>>> GetDescendantsAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null)
    {
        var anchor = await _holonStore.GetByIdAsync(id, default);
        if (anchor.IsError || anchor.Result == null)
            return new AZOAResult<IEnumerable<IHolon>> { IsError = true, Message = anchor.Message ?? "Holon not found." };
        if (!CanRead(anchor.Result, callerUserId))
            return new AZOAResult<IEnumerable<IHolon>> { IsError = true, Message = "Holon not found." };

        var descendants = await CollectDescendantsAsync(id);
        var readable = descendants.Where(h => CanRead(h, callerUserId)).ToList();
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

    public async Task<AZOAResult<int>> PropagateAsync(Guid id, HolonPropagateRequest request, Guid? userId = null, AZOARequest? providerRequest = null)
    {
        var rootResult = await _holonStore.GetByIdAsync(id, default);
        if (rootResult.IsError || rootResult.Result == null)
            return new AZOAResult<int> { IsError = true, Message = rootResult.Message ?? "Holon not found." };
        // Fail-closed: a mutating call with no acting user is a wiring bug, not an admin escape hatch.
        if (!userId.HasValue || !IsOwnedBy(rootResult.Result, userId.Value))
            return new AZOAResult<int> { IsError = true, Message = "Holon is owned by a different user." };

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

    public async Task<AZOAResult<HolonComposition>> ComposeAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null)
    {
        var rootResult = await _holonStore.GetByIdAsync(id, default);
        if (rootResult.IsError || rootResult.Result == null)
            return new AZOAResult<HolonComposition> { IsError = true, Message = "Holon not found." };
        // Own-or-public gate on the root: don't confirm a private holon's existence
        // or compose over another tenant's subtree.
        if (!CanRead(rootResult.Result, callerUserId))
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
                    if (CanRead(child, callerUserId))
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
            .Result?.Count(h => CanRead(h, callerUserId)) ?? 0;

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

    public async Task<AZOAResult<IHolon>> CloneAsync(Guid id, HolonCloneRequest request, Guid userId, AZOARequest? providerRequest = null)
    {
        var originalResult = await _holonStore.GetByIdAsync(id, default);
        if (originalResult.IsError || originalResult.Result == null)
            return new AZOAResult<IHolon> { IsError = true, Message = "Holon not found." };

        var original = (Holon)originalResult.Result;
        // H-2: cross-user clone is the marketplace TEMPLATE mechanic — only the
        // owner, or anyone when the source is IsPublic, may clone. A non-owner
        // cloning a private holon is a cross-tenant IP-theft vector; fail closed as
        // "not found" so a private holon's existence is not confirmed by id probing.
        if (!IsOwnedBy(original, userId) && !original.IsPublic)
            return new AZOAResult<IHolon> { IsError = true, Message = "Holon not found." };

        var idMap = new Dictionary<Guid, Guid>();

        // Clone the root
        var clone = new Holon
        {
            Id = Guid.NewGuid(),
            Name = request.Name ?? $"{original.Name} (Copy)",
            Description = original.Description,
            ParentHolonId = request.NewParentId,
            UserId = userId,
            ProviderName = original.ProviderName,
            ChainId = original.ChainId,
            AssetType = original.AssetType,
            TokenId = null, // cloned holon is not the same on-chain asset
            // cross-user clone omits private_* metadata keys — see hardening review M2.
            Metadata = CloneMetadataWithoutPrivateKeys(original.Metadata, original.Id),
            PeerHolonIds = new List<Guid>(original.PeerHolonIds),
            IsActive = original.IsActive,
            // Clone provenance: link back to the source holon and its owner (the
            // cross-user template mechanic — caller becomes owner via UserId above).
            SourceHolonId = original.Id,
            OriginUserId = original.UserId
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
                        UserId = userId,
                        ProviderName = child.ProviderName,
                        ChainId = child.ChainId,
                        AssetType = child.AssetType,
                        TokenId = null,
                        // cross-user clone omits private_* metadata keys — see hardening review M2.
                        Metadata = CloneMetadataWithoutPrivateKeys(child.Metadata, child.Id),
                        PeerHolonIds = new List<Guid>(),
                        IsActive = child.IsActive,
                        // Clone provenance: link back to the source child + its owner.
                        SourceHolonId = child.Id,
                        OriginUserId = child.UserId
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
    /// Cross-user clone is an intentionally open template mechanic, so private_*
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

    public async Task<AZOAResult<bool>> MoveSubtreeAsync(Guid id, Guid newParentId, Guid? userId = null, AZOARequest? request = null)
    {
        // FR-6 / AC-6a: guard via shared helper (MoveSubtree precedent).
        var cycleError = await EnsureNotDescendantAsync(id, newParentId, request);
        if (cycleError != null) return new AZOAResult<bool> { IsError = true, Message = cycleError };

        var holonResult = await _holonStore.GetByIdAsync(id, default);
        if (holonResult.IsError || holonResult.Result == null)
            return new AZOAResult<bool> { IsError = true, Message = "Holon not found." };
        // Fail-closed: a mutating call with no acting user is a wiring bug, not an admin escape hatch.
        if (!userId.HasValue || !IsOwnedBy(holonResult.Result, userId.Value))
            return new AZOAResult<bool> { IsError = true, Message = "Holon is owned by a different user." };

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

    /// <summary>
    /// OI-30 (remediation): this used to hand back the guard's own sentence, which names the
    /// action and the allowlist. The decision now arrives already scrubbed from the counter,
    /// so the only wording this can return is the consumer-safe one.
    /// </summary>
    private async Task<string?> EnsureGovernedAssetTypeAllowedAsync(string? assetType, string action)
    {
        var decision = await _nodeGovernance.EnsureAssetTypeAllowedAsync(assetType, action);
        return decision.IsRefused ? decision.ConsumerMessage : null;
    }

    /// <summary>What the vote-of-record ledger says about a governance proposal.</summary>
    private enum ProposalFreezeState
    {
        /// <summary>Not a proposal, or a proposal no vote could ever have been recorded against.</summary>
        NotFreezable,

        /// <summary>A proposal that could hold votes and holds none.</summary>
        Unclaimed,

        /// <summary>At least one live vote of record exists.</summary>
        Claimed,

        /// <summary>The ledger could not be consulted, so no answer may be assumed.</summary>
        Unknown,
    }

    /// <summary>
    /// Refusal sentence when an incoming write would move something a recorded vote
    /// depends on, or null when the write is free. The single entry point both mutation
    /// seams use. See Services/Governance/AGENTS.md #proposal-freeze.
    /// </summary>
    private async Task<string?> FrozenProposalRewriteErrorAsync(
        IHolon existing,
        IReadOnlyDictionary<string, string>? incomingMetadata,
        bool? incomingIsPublic,
        string? incomingAssetType,
        IEnumerable<string>? removedMetadataKeys = null)
    {
        if (!string.Equals(existing.AssetType, ConsumerGovernanceHolonTypes.ProposalAssetType, StringComparison.Ordinal))
            return null;

        var rewrite = ConsumerGovernanceHolonTypes.DescribeDecisionBearingRewrite(
            existing.Metadata,
            existing.IsPublic,
            existing.AssetType,
            incomingMetadata,
            incomingIsPublic,
            incomingAssetType,
            removedMetadataKeys);

        return await FrozenProposalErrorAsync(
            existing,
            rewrite is null ? null : $"a recorded vote already depends on {rewrite}");
    }

    /// <summary>
    /// Refusal sentence when <paramref name="what"/> would change a proposal that has
    /// already been voted on, or null when the change is free. A null
    /// <paramref name="what"/> means nothing decision-bearing is changing.
    /// See Services/Governance/AGENTS.md #proposal-freeze.
    /// </summary>
    private async Task<string?> FrozenProposalErrorAsync(IHolon existing, string? what)
    {
        if (what == null)
            return null;

        var state = await ProposalFreezeStateAsync(existing);
        return state switch
        {
            ProposalFreezeState.Claimed =>
                $"A governance proposal is frozen once its members start voting: {what}.",
            ProposalFreezeState.Unknown =>
                "A governance proposal cannot be changed while its vote-of-record ledger is unavailable.",
            _ => null,
        };
    }

    /// <summary>
    /// Reads the proposal's own claim domain out of its PRE-MERGE metadata and asks the
    /// vote-of-record ledger whether anybody has voted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The domain comes from the recorded proposal, never from the incoming write, so
    /// moving the electorate and the question in one request still consults the domain
    /// the existing votes live in.
    /// </para>
    /// <para>
    /// A proposal that cannot resolve a claim domain is NotFreezable, and that is a proof
    /// rather than a convenience: both governance nodes refuse before claiming unless the
    /// derived uniqueness key, the spelled scope and the recorded electorate are all
    /// present. It is also what lets GovernanceProposalCreate record the derived key on
    /// the proposal it has just created - at that instant the key is absent, so no claim
    /// can exist.
    /// </para>
    /// <para>
    /// An ABSENT ledger is Unknown, never Unclaimed. "The dependency was not wired, so the
    /// control did nothing" is the failure this repo has shipped six times.
    /// </para>
    /// </remarks>
    private async Task<ProposalFreezeState> ProposalFreezeStateAsync(IHolon existing)
    {
        if (!string.Equals(existing.AssetType, ConsumerGovernanceHolonTypes.ProposalAssetType, StringComparison.Ordinal))
            return ProposalFreezeState.NotFreezable;

        if (!ConsumerGovernanceHolonTypes.TryResolveClaimDomain(
                existing.Id, existing.Metadata, out var uniquenessScopeValue, out var operationKey))
            return ProposalFreezeState.NotFreezable;

        if (_voteOfRecord == null)
            return ProposalFreezeState.Unknown;

        var live = await _voteOfRecord.ListLiveByOperationAsync(uniquenessScopeValue, operationKey, default);
        if (live.IsError || live.Result == null)
            return ProposalFreezeState.Unknown;

        return live.Result.Any(claim => claim.ReleasedAt is null && !string.IsNullOrWhiteSpace(claim.UserId))
            ? ProposalFreezeState.Claimed
            : ProposalFreezeState.Unclaimed;
    }
}
