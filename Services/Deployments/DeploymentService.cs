using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Deployments;

/// <inheritdoc/>
public sealed partial class DeploymentService(IDeploymentStore store, IActionGraphBundleStore bundles,
    IActionStore actions, IHolonManager holons, DeploymentArtifactCompiler compiler,
    IBlockchainProviderFactory providers, IValueAccessService valueAccess, INodeGovernanceGuard governance,
    IFrozenActionRuntime runtime, IWalletStore wallets, ITenantConsentGate consent,
    IEcosystemStore ecosystems, IDappSeriesStore series, IDeploymentRecoveryAuditStore recoveryAudits) : IDeploymentService
{
    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentArtifactView>> GenerateAsync(Guid bundleId, Guid actor, Guid? tenant,
        DeploymentGenerateRequest request, CancellationToken ct = default)
    {
        var bundle = await bundles.GetByIdAsync(bundleId, ct);
        if (bundle.IsError || bundle.Result?.UserId != actor || actor == Guid.Empty)
            return Fail<DeploymentArtifactView>("Bundle not found or not owned by this caller.");
        if (request.Target.Kind is not ("PlatformRuntime" or "NativeContract")
            || !Enum.TryParse<ChainNetwork>(request.Target.Network, false, out _))
            return Fail<DeploymentArtifactView>("Select an explicit deployment target and network.");
        RuntimeArtifact? runtime = null;
        NativeArtifact? native = null;
        if (request.Target.Kind == "PlatformRuntime")
        {
            if (request.Native is not null || request.ActionIds is { Count: > 64 })
                return Fail<DeploymentArtifactView>("Platform generation requires ordered action occurrences.");
            var sources = new List<DeploymentSource>();
            var rootActions = request.ActionIds ?? [];
            for (var index = 0; index < rootActions.Count; index++)
            {
                var loaded = await actions.GetActionAsync(rootActions[index], ct);
                var action = loaded.Result;
                if (loaded.IsError || action is null || !CanRun(action, actor) || action.Status != ActionStatus.Active
                    || string.IsNullOrWhiteSpace(action.PublishedVersionHash)
                    || action.PublishedVersionHash != ActionPublishedVersion.ComputeHash(action))
                    return Fail<DeploymentArtifactView>("Every source must be a currently authorized published action with an intact revision.");
                sources.Add(new DeploymentSource(action.Id, index.ToString(CultureInfo.InvariantCulture), action.Version, action.PublishedVersionHash, action));
            }
            var references = new List<DeploymentReference>();
            var mappings = request.Mappings?.ToList() ?? [];
            var bundleReferences = new List<DeploymentBundleReference>();
            var nestedError = await AppendEcosystemSourcesAsync(bundleId, actor, sources, references, mappings, bundleReferences, ct);
            if (nestedError is not null) return Fail<DeploymentArtifactView>(nestedError);
            foreach (var id in bundle.Result.BoundHolonIds.Distinct().Order())
            {
                var reference = await holons.GetAsync(id, actor);
                if (reference.IsError || reference.Result is null) return Fail<DeploymentArtifactView>("A bound resource is not readable by this caller.");
                references.Add(new DeploymentReference(id, ReferenceHash(reference.Result)));
            }
            try { runtime = compiler.Compose(bundleId, actor, sources, references.Distinct().ToArray(), mappings) with { BundleReferences = bundleReferences.Distinct().ToArray() }; }
            catch (ArgumentException exception) { return Fail<DeploymentArtifactView>(exception.Message); }
            catch (JsonException) { return Fail<DeploymentArtifactView>("Source configuration is not valid JSON."); }
        }
        else
        {
            if (request.Native is null || request.ActionIds is { Count: > 0 })
                return Fail<DeploymentArtifactView>("Native deployment requires an explicit compiled artifact or supported source format.");
            var module = Module(request.Target);
            if (module is null) return Fail<DeploymentArtifactView>("The selected provider has no native operation module.");
            var validated = await module.ValidateArtifactAsync(request.Native, ct);
            if (validated.IsError || validated.Result is null) return Fail<DeploymentArtifactView>(validated.Message);
            native = validated.Result;
        }
        var payload = new DeploymentArtifactPayload(2, bundleId, actor, tenant, request.Target, runtime, native);
        (string PayloadJson, string PayloadHash, string? Loader, string ArtifactHash) compiled;
        try { compiled = DeploymentArtifactCompiler.Compile(payload); }
        catch (ArgumentException exception) { return Fail<DeploymentArtifactView>(exception.Message); }
        var saved = await store.AddArtifactAsync(new DeploymentArtifact
        {
            Id = compiled.ArtifactHash, OwnerUserId = actor.ToString("N"), TenantId = Tenant(tenant), BundleId = bundleId.ToString("N"),
            PayloadJson = compiled.PayloadJson, PayloadHash = compiled.PayloadHash, Loader = compiled.Loader,
            ArtifactHash = compiled.ArtifactHash, CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
        if (saved.PayloadJson != compiled.PayloadJson || saved.ArtifactHash != compiled.ArtifactHash || saved.Loader != compiled.Loader)
            throw new InvalidOperationException("Immutable deployment artifact identity collision.");
        bundle.Result.GeneratedCode = saved.Loader ?? saved.PayloadJson;
        bundle.Result.TargetChain = request.Target.ChainType;
        bundle.Result.DeploymentConfig = JsonSerializer.Serialize(new { formatVersion = 2, artifactId = saved.Id, artifactHash = saved.ArtifactHash, state = "Generated", target = request.Target });
        bundle.Result.ModifiedDate = DateTime.UtcNow;
        var projection = await bundles.UpsertAsync(bundle.Result, ct);
        if (projection.IsError) return Fail<DeploymentArtifactView>(projection.Message);
        return AZOAResult<DeploymentArtifactView>.Success(new(saved.Id, saved.PayloadHash, saved.ArtifactHash, request.Target, saved.Loader, Native: native));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentArtifactView>> GetArtifactAsync(string artifactId, Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        var artifact = await store.GetArtifactAsync(artifactId, ct);
        if (artifact is null || artifact.OwnerUserId != actor.ToString("N") || artifact.TenantId != Tenant(tenant))
            return Fail<DeploymentArtifactView>("Generated artifact not found for this caller.");
        var payload = Read<DeploymentArtifactPayload>(artifact.PayloadJson);
        return AZOAResult<DeploymentArtifactView>.Success(new(artifact.Id, artifact.PayloadHash, artifact.ArtifactHash,
            payload.Target, artifact.Loader, Native: payload.Native));
    }

    /// <inheritdoc/>
    public Task<AZOAResult<DeploymentReceipt>> StartAsync(Guid actor, Guid? tenant, string requestKey,
        DeploymentStartRequest request, CancellationToken ct = default)
        => StartCoreAsync(actor, tenant, requestKey, request, true, ct);

    /// <inheritdoc/>
    public async Task<AZOAResult<object>> QueryContractAsync(ContractQueryRequest request, CancellationToken ct = default)
    {
        if (!Enum.TryParse<ChainNetwork>(request.Network, false, out var network)) return Fail<object>("An explicit network is required.");
        var provider = providers.GetProvider(request.ChainType, network);
        return provider is not null && providers.TryGetModule<IReadOnlyContractModule>(provider, out var module)
            ? await module!.QueryContractAsync(request.Call, ct) : Fail<object>("The selected provider has no read-only contract query capability.");
    }

    /// <inheritdoc/>
    public Task<AZOAResult<DeploymentReceipt>> AdmitAsync(Guid actor, Guid? tenant, string requestKey,
        DeploymentStartRequest request, CancellationToken ct = default)
        => StartCoreAsync(actor, tenant, requestKey, request, false, ct);

    private async Task<AZOAResult<DeploymentReceipt>> StartCoreAsync(Guid actor, Guid? tenant, string requestKey,
        DeploymentStartRequest request, bool authorize, CancellationToken ct, ActionFrozenNodeInput? actionInput = null, string? actionHash = null)
    {
        if (request.NativeIntent?.Kind == "Recovery")
            return Fail<DeploymentReceipt>("Native recovery requires an exclusively linked original operation.");
        if (actor == Guid.Empty || string.IsNullOrWhiteSpace(requestKey) || requestKey.Length > 200 || requestKey.Any(char.IsControl))
            return Fail<DeploymentReceipt>("An authenticated actor and bounded Idempotency-Key are required.");
        var id = DeploymentIdentity.Operation(actor, tenant, requestKey);
        var existing = await store.GetAsync(id, ct);
        if (existing is { State: "Cancelled", Stage: "BeforeAdmission" })
            return existing.RequestJson == CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(request))
                ? AZOAResult<DeploymentReceipt>.Success(View(existing)) : Fail<DeploymentReceipt>("Idempotency-Key was cancelled for another immutable request.");
        var artifact = await store.GetArtifactAsync(request.ArtifactId, ct);
        if (artifact is null || artifact.OwnerUserId != actor.ToString("N") || artifact.TenantId != Tenant(tenant)
            || artifact.ArtifactHash != request.ArtifactHash)
            return Fail<DeploymentReceipt>("Generated artifact not found for this authority.");
        var payload = Read<DeploymentArtifactPayload>(artifact.PayloadJson);
        if (payload.Version != 2 || payload.Target != request.Target)
            return Fail<DeploymentReceipt>("Target changed; explicitly regenerate the artifact before deployment.");
        var bundleAccess = await CheckBundleAuthorityAsync(payload.BundleId, actor, ct);
        if (bundleAccess is not null && existing is null) return Fail<DeploymentReceipt>(bundleAccess);
        if (payload.Target.Kind == "PlatformRuntime")
        {
            if (request.NativeIntent is not null || payload.Runtime is null) return Fail<DeploymentReceipt>("Invalid platform activation request.");
            var permitted = await CheckRuntimeAsync(payload.Runtime, actor, ct);
            if (permitted is not null) return Fail<DeploymentReceipt>(permitted);
        }
        else
        {
            var intent = request.NativeIntent;
            if (intent is null || intent.Version != 1 || intent.OperationKey != id || intent.ArtifactHash != artifact.ArtifactHash
                || intent.ChainType != request.Target.ChainType || intent.Network != request.Target.Network
                || !ValidAuthority(intent, actor, tenant)
                || !ValidBudget(intent.Budget))
                return Fail<DeploymentReceipt>("Native deployment requires exact caller authority, artifact identity and budget authorization.");
            var quoted = await store.GetQuoteAsync(intent.Budget.QuoteId, ct);
            if (quoted is null || quoted.ActorUserId != actor.ToString("N") || quoted.TenantId != Tenant(tenant)
                || quoted.IntentHash != QuoteIntentHash(intent) || existing is null && actionInput is null && quoted.ExpiresAt <= DateTimeOffset.UtcNow)
                return Fail<DeploymentReceipt>("Native operation quote is absent, expired or belongs to another intent.");
            intent = intent with { Quote = Read<NativeOperationQuote>(quoted.QuoteJson) };
            request = request with { NativeIntent = intent };
            if (payload.Native is null || !MatchesNativeArtifact(intent.Input, payload.Native))
                return Fail<DeploymentReceipt>("Native operation does not bind the generated artifact.");
            var access = await valueAccess.RequireValueAccessAsync(actor, tenant, ct);
            if (access.IsError) return Fail<DeploymentReceipt>(access.Message);
            var allowed = await governance.EnsureChainAllowedAsync(request.Target.ChainType, "native-deployment", ct);
            if (!allowed.IsAllowed) return Fail<DeploymentReceipt>(allowed.ConsumerMessage);
        }
        var requestJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(request));
        var row = await store.ClaimAsync(new DeploymentOperation
        {
            Id = id, ActorUserId = actor.ToString("N"), TenantId = Tenant(tenant), ArtifactId = artifact.Id,
            ArtifactHash = artifact.ArtifactHash, IntentHash = CanonicalJson.HashCanonical(requestJson), RequestJson = requestJson,
            ActionRunId = actionInput?.RunId.ToString("N"), ActionNodeId = actionInput?.NodeId.ToString("N"), ActionIntentHash = actionHash,
            State = "Admitted", Stage = payload.Runtime is null ? "Plan" : "Activate", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);
        if (row is null) return Fail<DeploymentReceipt>("The action was cancelled, forked or changed before deployment admission.");
        if (row.State == "Cancelled" && row.Stage == "BeforeAdmission")
            return row.RequestJson == requestJson ? AZOAResult<DeploymentReceipt>.Success(View(row))
                : Fail<DeploymentReceipt>("Idempotency-Key was cancelled for another immutable request.");
        if (row.ActionRunId != actionInput?.RunId.ToString("N") || row.ActionNodeId != actionInput?.NodeId.ToString("N") || row.ActionIntentHash != actionHash)
            return Fail<DeploymentReceipt>("Deployment request belongs to another action authority.");
        if (row.IntentHash != CanonicalJson.HashCanonical(requestJson) || row.RequestJson != requestJson)
            return Fail<DeploymentReceipt>("Idempotency-Key belongs to another immutable deployment intent.");
        return authorize ? await AdvanceAsync(row, actor, tenant, true, ct)
            : AZOAResult<DeploymentReceipt>.Success(View(row));
    }

    private async Task<string?> CheckRuntimeAsync(RuntimeArtifact artifact, Guid actor, CancellationToken ct)
    {
        foreach (var source in artifact.Sources)
        {
            var current = await actions.GetActionAsync(source.ActionId, ct);
            if (current.IsError || current.Result is not { } action || !CanRun(action, actor)
                || action.Status != ActionStatus.Active || action.Version != source.Version
                || action.PublishedVersionHash != source.PublishedHash || ActionPublishedVersion.ComputeHash(action) != source.PublishedHash)
                return "A source revision or current permission changed; regenerate or restore access before invocation.";
        }
        foreach (var reference in artifact.References)
        {
            var current = await holons.GetAsync(reference.HolonId, actor);
            if (current.IsError || current.Result is null || ReferenceHash(current.Result) != reference.ContentHash)
                return "A bound resource identity or permission changed.";
        }
        foreach (var reference in artifact.BundleReferences ?? [])
        {
            var nested = await bundles.GetByIdAsync(reference.BundleId, ct);
            if (nested.IsError || nested.Result is null || !nested.Result.IsActive || nested.Result.UserId != actor && !nested.Result.IsPublic
                || !TryBundleArtifact(nested.Result.DeploymentConfig, out var hash) || hash != reference.ArtifactHash)
                return "A nested bundle revision or current permission changed; regenerate before invocation.";
        }
        return null;
    }

    private async Task<string?> CheckBundleAuthorityAsync(Guid bundleId, Guid actor, CancellationToken ct)
    {
        var bundle = await bundles.GetByIdAsync(bundleId, ct);
        return bundle.IsError || bundle.Result is null || !bundle.Result.IsActive || bundle.Result.UserId != actor
            ? "The bundle is no longer active or owned by this caller." : null;
    }

    private static bool MatchesNativeArtifact(JsonElement input, NativeArtifact expected)
    {
        if (input.ValueKind != JsonValueKind.Object) return false;
        var artifacts = input.EnumerateObject().Where(property => property.Name.Equals("artifact", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (artifacts.Length != 1) return false;
        var value = artifacts[0].Value;
        try
        {
            var actual = value.Deserialize<NativeArtifact>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return actual is not null && CanonicalJson.Hash(actual) == CanonicalJson.Hash(expected);
        }
        catch (JsonException) { return false; }
    }

    private static bool CanRun(AZOA.WebAPI.Models.Actions.ActionDefinition action, Guid actor)
        => action.UserId == actor || action.IsPublic && (action.RunAccess == ActionRunAccess.Open || action.InvitedUserIds.Contains(actor));
    private static DeploymentHolonWitness ResourceWitness(IHolon holon)
        => new(holon.Id, holon.Name, holon.Description, holon.ParentHolonId, holon.UserId, holon.ProviderName,
            holon.ChainId, holon.AssetType, holon.TokenId, holon.Metadata, holon.PeerHolonIds, holon.CreatedDate,
            holon.ModifiedDate, holon.IsActive, holon.SourceHolonId, holon.OriginUserId, holon.IsPublic);

    private static string ReferenceHash(IHolon holon) => CanonicalJson.Hash(ResourceWitness(holon));
    private INativeOperationModule? Module(DeploymentTarget target)
    {
        if (!Enum.TryParse<ChainNetwork>(target.Network, false, out var network)) return null;
        var provider = providers.GetProvider(target.ChainType, network);
        return providers.TryGetModule<INativeOperationModule>(provider, out var module) ? module : null;
    }
    private static bool ValidBudget(NativeBudget budget) => !string.IsNullOrWhiteSpace(budget.QuoteId)
        && Positive(budget.MaximumNetworkFee) && Positive(budget.MaximumLockedRent) && Positive(budget.MaximumValue);
    private static bool Positive(string text) => BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
        && amount >= 0 && amount.ToString(CultureInfo.InvariantCulture) == text;
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException("Deployment journal is unreadable.");
    private static string Tenant(Guid? tenant) => tenant?.ToString("N") ?? "";
    private static AZOAResult<T> Fail<T>(string message) => AZOAResult<T>.Failure(message);
}
