using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Ecosystem;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Deployments;
using PocoEcosystem = AZOA.WebAPI.Persistence.SurrealDb.Models.Ecosystem;
using PocoEcosystemNode = AZOA.WebAPI.Persistence.SurrealDb.Models.EcosystemNode;

namespace AZOA.WebAPI.Managers;

public class ActionGraphBundleManager : IActionGraphBundleManager
{
    private readonly IActionGraphBundleStore _bundleStore;
    private readonly IEcosystemStore _ecosystemStore;
    private readonly IDappSeriesStore _dappSeriesStore;
    private readonly IDeploymentService _deployments;

    public ActionGraphBundleManager(
        IActionGraphBundleStore bundleStore,
        IEcosystemStore ecosystemStore,
        IDappSeriesStore dappSeriesStore,
        IDeploymentService deployments)
    {
        _bundleStore = bundleStore;
        _ecosystemStore = ecosystemStore;
        _dappSeriesStore = dappSeriesStore;
        _deployments = deployments;
    }

    public async Task<AZOAResult<IActionGraphBundle>> GetAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null)
    {
        var result = await _bundleStore.GetByIdAsync(id, default);
        if (result.IsError || result.Result == null) return result;
        // Own-or-public read scope: don't confirm a private action graph bundle's existence to a
        // non-owner. See Controllers/AGENTS.md §cross-tenant-read-scope.
        if (!CanRead(result.Result, callerUserId))
            return new AZOAResult<IActionGraphBundle> { IsError = true, Message = "action graph bundle not found." };
        return result;
    }

    public async Task<AZOAResult<IEnumerable<IActionGraphBundle>>> GetAllAsync(Guid? callerUserId = null, AZOARequest? request = null)
    {
        var result = await _bundleStore.GetAllAsync(default);
        if (result.IsError || result.Result == null) return result;
        return new AZOAResult<IEnumerable<IActionGraphBundle>>
        {
            Result = result.Result.Where(s => CanRead(s, callerUserId)).ToList(),
            Message = result.Message
        };
    }

    // Cross-tenant read scope: an action graph bundle is readable iff the caller owns it OR it is
    // IsPublic. A null callerUserId fails closed (public-only).
    private static bool CanRead(IActionGraphBundle record, Guid? callerUserId) =>
        callerUserId.HasValue && record.UserId == callerUserId.Value || record.IsPublic;

    public async Task<AZOAResult<IActionGraphBundle>> CreateOrUpdateAsync(
        ActionGraphBundleCreateModel model,
        Guid userId,
        Guid? routeId = null,
        AZOARequest? request = null)
    {
        // IDOR-safe upsert:
        //   - PUT (routeId != null): load by id, then require IsOwnedBy(record, userId)
        //   - POST (routeId == null): load by (name, userId) — name collisions
        //     across users never overwrite each other.
        // The caller-supplied model.UserId is intentionally ignored — the
        // authenticated user id from the controller is the only source of truth.

        ActionGraphBundle bundle;
        if (routeId.HasValue)
        {
            var loaded = await _bundleStore.GetByIdAsync(routeId.Value, default);
            if (loaded.IsError || loaded.Result == null)
                return Fail(ActionGraphBundleAuthorizationError.NotFound + "action graph bundle not found.");

            if (!IsOwnedBy(loaded.Result, userId))
                return Fail(ActionGraphBundleAuthorizationError.Forbidden + "action graph bundle is owned by a different user.");

            bundle = (ActionGraphBundle)loaded.Result;
        }
        else
        {
            var match = await _bundleStore.GetByNameAndUserAsync(model.Name, userId, default);
            bundle = (match.Result as ActionGraphBundle) ?? new ActionGraphBundle { UserId = userId };
        }

        bundle.Name         = model.Name;
        bundle.Description  = model.Description;
        bundle.PublicKey    = model.PublicKey;
        bundle.UserId     = userId; // authoritative — never trust model.UserId
        bundle.ModifiedDate = DateTime.UtcNow;

        return await _bundleStore.UpsertAsync(bundle, default);
    }

    private static bool IsOwnedBy(IActionGraphBundle record, Guid userId) =>
        record.UserId.HasValue && record.UserId.Value == userId;

    private static AZOAResult<IActionGraphBundle> Fail(string message) =>
        new() { IsError = true, Message = message };

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid? userId = null, AZOARequest? request = null)
    {
        if (userId.HasValue)
        {
            var loaded = await _bundleStore.GetByIdAsync(id, default);
            if (loaded.IsError || loaded.Result == null)
                return new AZOAResult<bool> { IsError = true, Message = "action graph bundle not found." };
            if (!IsOwnedBy(loaded.Result, userId.Value))
                return new AZOAResult<bool> { IsError = true, Message = "action graph bundle is owned by a different user." };
        }

        return await _bundleStore.DeleteAsync(id, default);
    }

    public async Task<AZOAResult<IActionGraphBundle>> GenerateAsync(Guid id, ActionGraphBundleDappGenerationRequest request, Guid? userId = null, AZOARequest? providerRequest = null, Guid? actingTenantId = null)
    {
        var existing = await _bundleStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;
        // Fail-closed: a mutating call with no acting user is a wiring bug, not an admin escape hatch.
        if (!userId.HasValue || !IsOwnedBy(existing.Result, userId.Value))
            return Fail(ActionGraphBundleAuthorizationError.Forbidden + "action graph bundle is owned by a different user.");

        var bundle = (ActionGraphBundle)existing.Result;
        if (request.Generation is null) return Fail("Select an explicit versioned generation target; legacy configuration must be regenerated.");
        bundle.TargetChain = request.Generation.Target.ChainType;
        bundle.BoundHolonIds = request.BoundHolonIds;
        bundle.ModifiedDate = DateTime.UtcNow;
        var saved = await _bundleStore.UpsertAsync(bundle, default);
        if (saved.IsError) return saved;
        var generated = await _deployments.GenerateAsync(id, userId.Value, actingTenantId, request.Generation);
        return generated.IsError ? Fail(generated.Message) : await _bundleStore.GetByIdAsync(id);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentReceipt>> DeployAsync(Guid id, DeploymentStartRequest request, string requestKey,
        Guid userId, Guid? actingTenantId = null, CancellationToken ct = default)
    {
        var existing = await _bundleStore.GetByIdAsync(id, ct);
        if (existing.IsError || existing.Result is null || !IsOwnedBy(existing.Result, userId))
            return AZOAResult<DeploymentReceipt>.Failure("Bundle not found or not owned by this caller.");
        if (string.IsNullOrWhiteSpace(existing.Result.DeploymentConfig))
            return AZOAResult<DeploymentReceipt>.Failure("Generate an immutable artifact before deploying.");
        try
        {
            using var descriptor = JsonDocument.Parse(existing.Result.DeploymentConfig);
            if (!descriptor.RootElement.TryGetProperty("formatVersion", out var version) || version.GetInt32() != 2
                || !descriptor.RootElement.TryGetProperty("artifactId", out var artifact) || artifact.GetString() != request.ArtifactId)
                return AZOAResult<DeploymentReceipt>.Failure("Selected artifact is not this bundle's generated revision; regenerate or select its exact artifact.");
        }
        catch (JsonException) { return AZOAResult<DeploymentReceipt>.Failure("Legacy deployment configuration must be regenerated."); }
        return await _deployments.StartAsync(userId, actingTenantId, requestKey, request, ct);
    }

    // ── Ecosystem tree (D2) ─────────────────────────────────────────────────

    public async Task<AZOAResult<EcosystemTree>> AddDappSeriesAsync(
        Guid actionGraphBundleId, AddDappSeriesRequest request, Guid? userId = null, AZOARequest? providerRequest = null)
    {
        // 1. Load + own-check the ActionGraphBundle (IDOR: scope by route id + authenticated user).
        var bundleResult = await _bundleStore.GetByIdAsync(actionGraphBundleId, default);
        if (bundleResult.IsError || bundleResult.Result == null)
            return FailTree(ActionGraphBundleAuthorizationError.NotFound + "action graph bundle not found.");
        if (userId.HasValue && !IsOwnedBy(bundleResult.Result, userId.Value))
            return FailTree(ActionGraphBundleAuthorizationError.Forbidden + "action graph bundle is owned by a different user.");
        var bundle = (ActionGraphBundle)bundleResult.Result;
        var ownerUser = userId ?? bundle.UserId ?? Guid.Empty;

        // 2. Validate the attached reference exists AND is owned by the same user.
        //    Caller-supplied owner ids are never trusted — ownership is re-checked here.
        var refError = await ValidateRefOwnershipAsync(request.RefKind, request.RefId, userId);
        if (refError != null) return FailTree(refError);

        // 3. Resolve (or lazily create) the ecosystem for this ActionGraphBundle.
        var ecoResult = await _ecosystemStore.GetByActionGraphBundleAsync(actionGraphBundleId, default);
        if (ecoResult.IsError) return FailTree(ecoResult.Message ?? "Failed to load ecosystem.");

        PocoEcosystem eco;
        if (ecoResult.Result == null)
        {
            eco = new PocoEcosystem
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(request.EcosystemName) ? $"{bundle.Name} Ecosystem" : request.EcosystemName!,
                ActionGraphBundleId = actionGraphBundleId.ToString("N"),
                UserId = ownerUser.ToString("N"),
                TargetChain = request.TargetChain ?? bundle.TargetChain,
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
            RefKind = request.RefKind == EcosystemRefKind.ActionGraphBundle
                ? PocoEcosystemNode.RefKindValue.ActionGraphBundle
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

        bundle.GeneratedCode = null;
        bundle.DeploymentConfig = null;
        bundle.ModifiedDate = DateTime.UtcNow;
        await _bundleStore.UpsertAsync(bundle, default);

        return new AZOAResult<EcosystemTree> { Result = tree, Message = "DappSeries attached to ecosystem." };
    }

    public async Task<AZOAResult<EcosystemTree>> GetEcosystemAsync(
        Guid actionGraphBundleId, Guid? userId = null, AZOARequest? providerRequest = null)
    {
        var bundleResult = await _bundleStore.GetByIdAsync(actionGraphBundleId, default);
        if (bundleResult.IsError || bundleResult.Result == null)
            return FailTree(ActionGraphBundleAuthorizationError.NotFound + "action graph bundle not found.");
        if (userId.HasValue && !IsOwnedBy(bundleResult.Result, userId.Value))
            return FailTree(ActionGraphBundleAuthorizationError.Forbidden + "action graph bundle is owned by a different user.");

        var ecoResult = await _ecosystemStore.GetByActionGraphBundleAsync(actionGraphBundleId, default);
        if (ecoResult.IsError) return FailTree(ecoResult.Message ?? "Failed to load ecosystem.");
        if (ecoResult.Result == null)
            return new AZOAResult<EcosystemTree> { Result = null, Message = "No ecosystem for this action graph bundle." };

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

    /// <summary>Confirms the attached DappSeries/ActionGraphBundle exists and is owned by
    /// the calling user. Returns an error message or null when valid.</summary>
    private async Task<string?> ValidateRefOwnershipAsync(EcosystemRefKind kind, Guid refId, Guid? userId)
    {
        if (kind == EcosystemRefKind.DappSeries)
        {
            var series = await _dappSeriesStore.GetSeriesAsync(refId, default);
            if (series.IsError || series.Result == null)
                return $"DappSeries {refId} not found.";
            if (userId.HasValue && series.Result.UserId != userId.Value.ToString("N"))
                return ActionGraphBundleAuthorizationError.Forbidden + "DappSeries is owned by a different user.";
            return null;
        }

        // Nested ActionGraphBundle reference.
        var bundle = await _bundleStore.GetByIdAsync(refId, default);
        if (bundle.IsError || bundle.Result == null)
            return $"action graph bundle {refId} not found.";
        if (userId.HasValue && !IsOwnedBy(bundle.Result, userId.Value))
            return ActionGraphBundleAuthorizationError.Forbidden + "Referenced action graph bundle is owned by a different user.";
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

    private static EcosystemModel ToEcosystemModel(PocoEcosystem e) => new()
    {
        Id = Guid.ParseExact(e.Id, "N"),
        Name = e.Name,
        Description = e.Description,
        ActionGraphBundleId = ParseHexOrEmpty(e.ActionGraphBundleId),
        UserId = ParseHexOrEmpty(e.UserId),
        TargetChain = e.TargetChain,
        CreatedDate = e.CreatedDate.UtcDateTime,
        ModifiedDate = e.ModifiedDate?.UtcDateTime,
    };

    private static EcosystemNodeModel ToNodeModel(PocoEcosystemNode n) => new()
    {
        Id = Guid.ParseExact(n.Id, "N"),
        EcosystemId = ParseHexOrEmpty(n.EcosystemId),
        ParentNodeId = string.IsNullOrEmpty(n.ParentNodeId) ? null : Guid.ParseExact(n.ParentNodeId, "N"),
        RefKind = n.RefKind == PocoEcosystemNode.RefKindValue.ActionGraphBundle ? EcosystemRefKind.ActionGraphBundle : EcosystemRefKind.DappSeries,
        RefId = ParseHexOrEmpty(n.RefId),
        Label = n.Label,
        CreatedDate = n.CreatedDate.UtcDateTime,
    };

    private static Guid ParseHexOrEmpty(string? hex) =>
        !string.IsNullOrEmpty(hex) && Guid.TryParseExact(hex, "N", out var g) ? g : Guid.Empty;
}
