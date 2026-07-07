using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Ecosystem;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using PocoEcosystem = AZOA.WebAPI.Persistence.SurrealDb.Models.Ecosystem;
using PocoEcosystemNode = AZOA.WebAPI.Persistence.SurrealDb.Models.EcosystemNode;

namespace AZOA.WebAPI.Managers;

public class STARManager : ISTARManager
{
    private readonly ISTARStore _starStore;
    private readonly IEcosystemStore _ecosystemStore;
    private readonly IDappSeriesStore _dappSeriesStore;

    public STARManager(
        ISTARStore starStore,
        IEcosystemStore ecosystemStore,
        IDappSeriesStore dappSeriesStore)
    {
        _starStore = starStore;
        _ecosystemStore = ecosystemStore;
        _dappSeriesStore = dappSeriesStore;
    }

    public async Task<AZOAResult<ISTARODK>> GetAsync(Guid id, Guid? callerAvatarId = null, AZOARequest? request = null)
    {
        var result = await _starStore.GetByIdAsync(id, default);
        if (result.IsError || result.Result == null) return result;
        // Own-or-public read scope: don't confirm a private STAR ODK's existence to a
        // non-owner. See Controllers/AGENTS.md §cross-tenant-read-scope.
        if (!CanRead(result.Result, callerAvatarId))
            return new AZOAResult<ISTARODK> { IsError = true, Message = "STAR ODK not found." };
        return result;
    }

    public async Task<AZOAResult<IEnumerable<ISTARODK>>> GetAllAsync(Guid? callerAvatarId = null, AZOARequest? request = null)
    {
        var result = await _starStore.GetAllAsync(default);
        if (result.IsError || result.Result == null) return result;
        return new AZOAResult<IEnumerable<ISTARODK>>
        {
            Result = result.Result.Where(s => CanRead(s, callerAvatarId)).ToList(),
            Message = result.Message
        };
    }

    // Cross-tenant read scope: a STAR ODK is readable iff the caller owns it OR it is
    // IsPublic. A null callerAvatarId fails closed (public-only).
    private static bool CanRead(ISTARODK record, Guid? callerAvatarId) =>
        callerAvatarId.HasValue && record.AvatarId == callerAvatarId.Value || record.IsPublic;

    public async Task<AZOAResult<ISTARODK>> CreateOrUpdateAsync(
        STARODKCreateModel model,
        Guid avatarId,
        Guid? routeId = null,
        AZOARequest? request = null)
    {
        // IDOR-safe upsert:
        //   - PUT (routeId != null): load by id, then require IsOwnedBy(record, avatarId)
        //   - POST (routeId == null): load by (name, avatarId) — name collisions
        //     across avatars never overwrite each other.
        // The caller-supplied model.AvatarId is intentionally ignored — the
        // authenticated avatar id from the controller is the only source of truth.

        STARODK odk;
        if (routeId.HasValue)
        {
            var loaded = await _starStore.GetByIdAsync(routeId.Value, default);
            if (loaded.IsError || loaded.Result == null)
                return Fail(STARODKAuthorizationError.NotFound + "STAR ODK not found.");

            if (!IsOwnedBy(loaded.Result, avatarId))
                return Fail(STARODKAuthorizationError.Forbidden + "STAR ODK is owned by a different avatar.");

            odk = (STARODK)loaded.Result;
        }
        else
        {
            var match = await _starStore.GetByNameAndAvatarAsync(model.Name, avatarId, default);
            odk = (match.Result as STARODK) ?? new STARODK { AvatarId = avatarId };
        }

        odk.Name         = model.Name;
        odk.Description  = model.Description;
        odk.PublicKey    = model.PublicKey;
        odk.AvatarId     = avatarId; // authoritative — never trust model.AvatarId
        odk.ModifiedDate = DateTime.UtcNow;

        return await _starStore.UpsertAsync(odk, default);
    }

    private static bool IsOwnedBy(ISTARODK record, Guid avatarId) =>
        record.AvatarId.HasValue && record.AvatarId.Value == avatarId;

    private static AZOAResult<ISTARODK> Fail(string message) =>
        new() { IsError = true, Message = message };

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid? avatarId = null, AZOARequest? request = null)
    {
        if (avatarId.HasValue)
        {
            var loaded = await _starStore.GetByIdAsync(id, default);
            if (loaded.IsError || loaded.Result == null)
                return new AZOAResult<bool> { IsError = true, Message = "STAR ODK not found." };
            if (!IsOwnedBy(loaded.Result, avatarId.Value))
                return new AZOAResult<bool> { IsError = true, Message = "STAR ODK is owned by a different avatar." };
        }

        return await _starStore.DeleteAsync(id, default);
    }

    public async Task<AZOAResult<ISTARODK>> GenerateAsync(Guid id, STARDappGenerationRequest request, Guid? avatarId = null, AZOARequest? providerRequest = null)
    {
        var existing = await _starStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;
        // Fail-closed: a mutating call with no acting avatar is a wiring bug, not an admin escape hatch.
        if (!avatarId.HasValue || !IsOwnedBy(existing.Result, avatarId.Value))
            return Fail(STARODKAuthorizationError.Forbidden + "STAR ODK is owned by a different avatar.");

        var odk = (STARODK)existing.Result;
        odk.TargetChain = request.TargetChain;
        odk.BoundHolonIds = request.BoundHolonIds;
        odk.GeneratedCode = GenerateDappCode(odk, request);
        odk.ModifiedDate = DateTime.UtcNow;

        return await _starStore.UpsertAsync(odk, default);
    }

    public async Task<AZOAResult<ISTARODK>> DeployAsync(Guid id, Guid? avatarId = null, AZOARequest? providerRequest = null)
    {
        var existing = await _starStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;
        // Fail-closed: a mutating call with no acting avatar is a wiring bug, not an admin escape hatch.
        if (!avatarId.HasValue || !IsOwnedBy(existing.Result, avatarId.Value))
            return Fail(STARODKAuthorizationError.Forbidden + "STAR ODK is owned by a different avatar.");

        var odk = (STARODK)existing.Result;
        if (string.IsNullOrEmpty(odk.GeneratedCode))
            return new AZOAResult<ISTARODK> { IsError = true, Message = "Dapp must be generated before deployment." };

        odk.DeploymentConfig = JsonSerializer.Serialize(new
        {
            DeployedAt = DateTime.UtcNow,
            Chain = odk.TargetChain,
            Holons = odk.BoundHolonIds,
            TxHash = $"0x{Guid.NewGuid():N}"
        });
        odk.ModifiedDate = DateTime.UtcNow;

        return await _starStore.UpsertAsync(odk, default);
    }

    private static string GenerateDappCode(ISTARODK odk, STARDappGenerationRequest request)
    {
        var config = new
        {
            Name = odk.Name,
            Description = odk.Description,
            TargetChain = request.TargetChain,
            BoundHolons = request.BoundHolonIds,
            UserConfig = request.Config,
            GeneratedAt = DateTime.UtcNow
        };
        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Ecosystem tree (D2) ─────────────────────────────────────────────────

    public async Task<AZOAResult<EcosystemTree>> AddDappSeriesAsync(
        Guid starOdkId, AddDappSeriesRequest request, Guid? avatarId = null, AZOARequest? providerRequest = null)
    {
        // 1. Load + own-check the STARODK (IDOR: scope by route id + authenticated avatar).
        var starResult = await _starStore.GetByIdAsync(starOdkId, default);
        if (starResult.IsError || starResult.Result == null)
            return FailTree(STARODKAuthorizationError.NotFound + "STAR ODK not found.");
        if (avatarId.HasValue && !IsOwnedBy(starResult.Result, avatarId.Value))
            return FailTree(STARODKAuthorizationError.Forbidden + "STAR ODK is owned by a different avatar.");
        var star = (STARODK)starResult.Result;
        var ownerAvatar = avatarId ?? star.AvatarId ?? Guid.Empty;

        // 2. Validate the attached reference exists AND is owned by the same avatar.
        //    Caller-supplied owner ids are never trusted — ownership is re-checked here.
        var refError = await ValidateRefOwnershipAsync(request.RefKind, request.RefId, avatarId);
        if (refError != null) return FailTree(refError);

        // 3. Resolve (or lazily create) the ecosystem for this STARODK.
        var ecoResult = await _ecosystemStore.GetByStarOdkAsync(starOdkId, default);
        if (ecoResult.IsError) return FailTree(ecoResult.Message ?? "Failed to load ecosystem.");

        PocoEcosystem eco;
        if (ecoResult.Result == null)
        {
            eco = new PocoEcosystem
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(request.EcosystemName) ? $"{star.Name} Ecosystem" : request.EcosystemName!,
                StarOdkId = starOdkId.ToString("N"),
                AvatarId = ownerAvatar.ToString("N"),
                TargetChain = request.TargetChain ?? star.TargetChain,
                CreatedDate = DateTimeOffset.UtcNow,
            };
            var savedEco = await _ecosystemStore.UpsertAsync(eco, default);
            if (savedEco.IsError || savedEco.Result == null) return FailTree(savedEco.Message ?? "Failed to create ecosystem.");
            eco = savedEco.Result;
        }
        else
        {
            eco = ecoResult.Result;
        }

        var ecosystemId = Guid.ParseExact(eco.Id, "N");

        // 4. Load existing nodes; validate the parent (if any) and guard cycles.
        var nodesResult = await _ecosystemStore.GetNodesAsync(ecosystemId, default);
        if (nodesResult.IsError) return FailTree(nodesResult.Message ?? "Failed to load ecosystem nodes.");
        var existing = nodesResult.Result?.ToList() ?? new List<PocoEcosystemNode>();

        Guid? parentNodeId = request.ParentNodeId;
        if (parentNodeId.HasValue)
        {
            var parentHex = parentNodeId.Value.ToString("N");
            if (!existing.Any(n => n.Id == parentHex))
                return FailTree("Parent node does not belong to this ecosystem.");
        }

        // 5. Persist the new node.
        var newNode = new PocoEcosystemNode
        {
            Id = Guid.NewGuid().ToString("N"),
            EcosystemId = eco.Id,
            ParentNodeId = parentNodeId?.ToString("N"),
            RefKind = request.RefKind == EcosystemRefKind.StarOdk
                ? PocoEcosystemNode.RefKindValue.StarOdk
                : PocoEcosystemNode.RefKindValue.DappSeries,
            RefId = request.RefId.ToString("N"),
            Label = request.Label,
            CreatedDate = DateTimeOffset.UtcNow,
        };
        var savedNode = await _ecosystemStore.UpsertNodeAsync(newNode, default);
        if (savedNode.IsError || savedNode.Result == null) return FailTree(savedNode.Message ?? "Failed to attach node.");
        existing.Add(savedNode.Result);

        // 6. Assemble the tree (this also validates acyclicity) and regenerate codegen.
        var tree = AssembleTree(eco, existing, out var cycleError);
        if (cycleError != null) return FailTree(cycleError);

        star.GeneratedCode = GenerateEcosystemCode(star, tree);
        star.ModifiedDate = DateTime.UtcNow;
        await _starStore.UpsertAsync(star, default);

        return new AZOAResult<EcosystemTree> { Result = tree, Message = "DappSeries attached to ecosystem." };
    }

    public async Task<AZOAResult<EcosystemTree>> GetEcosystemAsync(
        Guid starOdkId, Guid? avatarId = null, AZOARequest? providerRequest = null)
    {
        var starResult = await _starStore.GetByIdAsync(starOdkId, default);
        if (starResult.IsError || starResult.Result == null)
            return FailTree(STARODKAuthorizationError.NotFound + "STAR ODK not found.");
        if (avatarId.HasValue && !IsOwnedBy(starResult.Result, avatarId.Value))
            return FailTree(STARODKAuthorizationError.Forbidden + "STAR ODK is owned by a different avatar.");

        var ecoResult = await _ecosystemStore.GetByStarOdkAsync(starOdkId, default);
        if (ecoResult.IsError) return FailTree(ecoResult.Message ?? "Failed to load ecosystem.");
        if (ecoResult.Result == null)
            return new AZOAResult<EcosystemTree> { Result = null, Message = "No ecosystem for this STAR ODK." };

        var eco = ecoResult.Result;
        var ecosystemId = Guid.ParseExact(eco.Id, "N");
        var nodesResult = await _ecosystemStore.GetNodesAsync(ecosystemId, default);
        var nodes = nodesResult.Result?.ToList() ?? new List<PocoEcosystemNode>();

        var tree = AssembleTree(eco, nodes, out var cycleError);
        if (cycleError != null) return FailTree(cycleError);
        return new AZOAResult<EcosystemTree> { Result = tree, Message = "Success" };
    }

    // ── Ecosystem helpers ───────────────────────────────────────────────────

    private static AZOAResult<EcosystemTree> FailTree(string message) =>
        new() { IsError = true, Message = message };

    /// <summary>Confirms the attached DappSeries/STARODK exists and is owned by
    /// the calling avatar. Returns an error message or null when valid.</summary>
    private async Task<string?> ValidateRefOwnershipAsync(EcosystemRefKind kind, Guid refId, Guid? avatarId)
    {
        if (kind == EcosystemRefKind.DappSeries)
        {
            var series = await _dappSeriesStore.GetSeriesAsync(refId, default);
            if (series.IsError || series.Result == null)
                return $"DappSeries {refId} not found.";
            if (avatarId.HasValue && series.Result.AvatarId != avatarId.Value.ToString("N"))
                return STARODKAuthorizationError.Forbidden + "DappSeries is owned by a different avatar.";
            return null;
        }

        // Nested STARODK reference.
        var star = await _starStore.GetByIdAsync(refId, default);
        if (star.IsError || star.Result == null)
            return $"STAR ODK {refId} not found.";
        if (avatarId.HasValue && !IsOwnedBy(star.Result, avatarId.Value))
            return STARODKAuthorizationError.Forbidden + "Referenced STAR ODK is owned by a different avatar.";
        return null;
    }

    /// <summary>
    /// Folds a flat node list into a parent/children tree rooted on the ecosystem.
    /// Guards against a parent chain that never reaches a root (a cycle): mirrors
    /// the holon parent-cycle precedent by tracking visited ids while walking each
    /// node up to its root. Sets <paramref name="cycleError"/> (and returns a
    /// partial tree) when a cycle is detected.
    /// See Managers/AGENTS.md §ecosystem-tree.
    /// </summary>
    private static EcosystemTree AssembleTree(
        PocoEcosystem eco, List<PocoEcosystemNode> nodes, out string? cycleError)
    {
        cycleError = null;

        var models = nodes.Select(ToNodeModel).ToList();
        var byId = models.ToDictionary(m => m.Id);

        // Cycle guard: every node must reach a null parent within |nodes| hops.
        foreach (var m in models)
        {
            var visited = new HashSet<Guid> { m.Id };
            var cursorId = m.ParentNodeId;
            while (cursorId.HasValue)
            {
                if (!byId.ContainsKey(cursorId.Value)) break; // parent outside set → treat as root
                if (!visited.Add(cursorId.Value))
                {
                    cycleError = "Ecosystem tree contains a cycle in the parent chain.";
                    break;
                }
                cursorId = byId[cursorId.Value].ParentNodeId;
            }
            if (cycleError != null) break;
        }

        var treeNodes = models.ToDictionary(m => m.Id, m => new EcosystemTreeNode { Node = m });
        var roots = new List<EcosystemTreeNode>();
        foreach (var m in models)
        {
            var tn = treeNodes[m.Id];
            if (m.ParentNodeId.HasValue && treeNodes.TryGetValue(m.ParentNodeId.Value, out var parent))
                parent.Children.Add(tn);
            else
                roots.Add(tn);
        }

        return new EcosystemTree { Ecosystem = ToEcosystemModel(eco), Roots = roots };
    }

    /// <summary>
    /// Tree-walking multi-dApp codegen. Composes the single-dApp descriptor
    /// (mirrors <see cref="GenerateDappCode"/>) across every node in the tree,
    /// depth-first, producing one composed JSON artifact stored on the owning
    /// STARODK's GeneratedCode. Real cross-chain value in the composed tree flows
    /// through the Phase-B bridge (Algorand real; Solana fail-closed) — the
    /// descriptor records the target chain but does not itself move value.
    /// </summary>
    private static string GenerateEcosystemCode(STARODK star, EcosystemTree tree)
    {
        var composed = new
        {
            Ecosystem = tree.Ecosystem.Name,
            OwnerStarOdk = star.Name,
            TargetChain = tree.Ecosystem.TargetChain,
            GeneratedAt = DateTime.UtcNow,
            Dapps = tree.Roots.Select(WalkNode).ToList(),
        };
        return JsonSerializer.Serialize(composed, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object WalkNode(EcosystemTreeNode tn) => new
    {
        NodeId = tn.Node.Id,
        Kind = tn.Node.RefKind.ToString(),
        RefId = tn.Node.RefId,
        Label = tn.Node.Label,
        Children = tn.Children.Select(WalkNode).ToList(),
    };

    private static EcosystemModel ToEcosystemModel(PocoEcosystem e) => new()
    {
        Id = Guid.ParseExact(e.Id, "N"),
        Name = e.Name,
        Description = e.Description,
        StarOdkId = ParseHexOrEmpty(e.StarOdkId),
        AvatarId = ParseHexOrEmpty(e.AvatarId),
        TargetChain = e.TargetChain,
        CreatedDate = e.CreatedDate.UtcDateTime,
        ModifiedDate = e.ModifiedDate?.UtcDateTime,
    };

    private static EcosystemNodeModel ToNodeModel(PocoEcosystemNode n) => new()
    {
        Id = Guid.ParseExact(n.Id, "N"),
        EcosystemId = ParseHexOrEmpty(n.EcosystemId),
        ParentNodeId = string.IsNullOrEmpty(n.ParentNodeId) ? null : Guid.ParseExact(n.ParentNodeId, "N"),
        RefKind = n.RefKind == PocoEcosystemNode.RefKindValue.StarOdk ? EcosystemRefKind.StarOdk : EcosystemRefKind.DappSeries,
        RefId = ParseHexOrEmpty(n.RefId),
        Label = n.Label,
        CreatedDate = n.CreatedDate.UtcDateTime,
    };

    private static Guid ParseHexOrEmpty(string? hex) =>
        !string.IsNullOrEmpty(hex) && Guid.TryParseExact(hex, "N", out var g) ? g : Guid.Empty;
}
