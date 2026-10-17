using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Deployments;

public sealed partial class DeploymentService
{
    /// <inheritdoc/>
    public Task<AZOAResult<DeploymentReceipt>> GetRequestAsync(string requestKey, Guid actor, Guid? tenant, CancellationToken ct = default)
        => GetAsync(DeploymentIdentity.Operation(actor, tenant, requestKey), actor, tenant, ct);

    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentReceipt>> CancelRequestAsync(DeploymentRequestCancellation request, Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        if (actor == Guid.Empty || string.IsNullOrWhiteSpace(request.RequestKey) || request.RequestKey.Length > 200
            || request.RequestKey.Any(char.IsControl) || request.Request.Target.Kind is not ("PlatformRuntime" or "NativeContract"))
            return Fail<DeploymentReceipt>("A bounded request key and explicit target are required.");
        var id = DeploymentIdentity.Operation(actor, tenant, request.RequestKey);
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(request.Request));
        var row = await store.ClaimAsync(new DeploymentOperation
        {
            Id = id, ActorUserId = actor.ToString("N"), TenantId = Tenant(tenant), ArtifactId = request.Request.ArtifactId,
            ArtifactHash = request.Request.ArtifactHash, IntentHash = CanonicalJson.HashCanonical(json), RequestJson = json,
            State = "Cancelled", Stage = "BeforeAdmission", Detail = "Request cancelled before admission; this key cannot authorize a later send.",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);
        if (row is null) return Fail<DeploymentReceipt>("Cancellation could not be claimed.");
        if (row.RequestJson != json || row.IntentHash != CanonicalJson.HashCanonical(json))
            return Fail<DeploymentReceipt>("Cancellation request does not match the immutable request already bound to this key.");
        return row.State == "Cancelled" ? AZOAResult<DeploymentReceipt>.Success(View(row)) : await CancelAsync(id, actor, tenant, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentReceipt>> GetAsync(string operationId, Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        var row = await OwnedAsync(operationId, actor, tenant, ct);
        return row is null ? Fail<DeploymentReceipt>("Deployment not found.") : AZOAResult<DeploymentReceipt>.Success(View(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentPage>> ListAsync(Guid actor, Guid? tenant, string? cursor = null, int limit = 25, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var page = new DiscoveryCursor(actor.ToString("N"), Tenant(tenant), DateTimeOffset.UtcNow, 0);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            try { page = Read<DiscoveryCursor>(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor))); }
            catch (Exception exception) when (exception is FormatException or JsonException) { return Fail<DeploymentPage>("Invalid discovery cursor."); }
            if (page.Actor != actor.ToString("N") || page.Tenant != Tenant(tenant) || page.Offset is < 0 or > 1_000_000
                || page.Snapshot > DateTimeOffset.UtcNow) return Fail<DeploymentPage>("Discovery cursor belongs to another scope.");
        }
        var rows = await store.ListActorAsync(page.Actor, page.Tenant, page.Snapshot, page.Offset, limit + 1, ct);
        var next = rows.Count > limit ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(page with { Offset = page.Offset + limit }))) : null;
        return AZOAResult<DeploymentPage>.Success(new(rows.Take(limit).Select(View).ToArray(), next));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentReceipt>> ContinueAsync(string operationId, Guid actor, Guid? tenant, CancellationToken ct = default,
        SigningCredentialWitness? credential = null)
    {
        var row = await OwnedAsync(operationId, actor, tenant, ct);
        return row is null ? Fail<DeploymentReceipt>("Deployment not found.") : await AdvanceAsync(row, actor, tenant, true, ct, credential);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentReceipt>> CancelAsync(string operationId, Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        var row = await OwnedAsync(operationId, actor, tenant, ct);
        if (row is null) return Fail<DeploymentReceipt>("Deployment not found.");
        if (row.State == "Cancelled") return AZOAResult<DeploymentReceipt>.Success(View(row));
        if (row.State is not ("Admitted" or "AwaitingAuthorization" or "Preparing" or "Prepared") || row.SubmissionAuthorized
            || Read<List<NativeStageHistory>>(row.HistoryJson).Any(history => history.Observation.Verdict == "Confirmed"))
            return Fail<DeploymentReceipt>("Reconcile pending transactions and explicitly recover any created resources before cancellation.");
        row.State = "Cancelled";
        row.PreparedJson = null;
        row.Detail = "Request cancelled; no native effect completed.";
        var saved = await store.UpdateAsync(row, row.Version, ct) ?? await RequireCurrentAsync(row.Id, ct);
        return AZOAResult<DeploymentReceipt>.Success(View(saved));
    }

    /// <inheritdoc/>
    public async Task ObserveAsync(string operationId, CancellationToken ct = default)
    {
        var row = await store.GetAsync(operationId, ct);
        if (row is { State: "Confirmed", ParentOperationId: not null, RecoveryProjected: false })
        {
            await ProjectRecoveryAsync(row, ct);
            return;
        }
        if (row is null || row.PreparedJson is null || !row.SubmissionAuthorized) return;
        await AdvanceAsync(row, Guid.Empty, null, false, ct);
    }

    private async Task<AZOAResult<DeploymentReceipt>> AdvanceAsync(DeploymentOperation row, Guid actor, Guid? tenant,
        bool authorizeNewStage, CancellationToken ct, SigningCredentialWitness? credential = null)
    {
        if (row.ReplacedByOperationId is not null) return AZOAResult<DeploymentReceipt>.Success(View(row));
        if (row.State is "Confirmed" or "Cancelled" or "Failed" or "AbortedClean" or "FailedClean")
        {
            if (row.State == "Confirmed") row = await ProjectRecoveryAsync(row, ct);
            return AZOAResult<DeploymentReceipt>.Success(View(row));
        }
        var request = Read<DeploymentStartRequest>(row.RequestJson);
        if (authorizeNewStage && !row.SubmissionAuthorized)
        {
            var source = await store.GetArtifactAsync(row.ArtifactId, ct) ?? throw new InvalidOperationException("Deployment artifact disappeared.");
            var bundleError = await CheckBundleAuthorityAsync(Read<DeploymentArtifactPayload>(source.PayloadJson).BundleId, actor, ct);
            if (bundleError is not null) return Fail<DeploymentReceipt>(bundleError);
        }
        if (request.Target.Kind == "PlatformRuntime")
        {
            if (!authorizeNewStage) return AZOAResult<DeploymentReceipt>.Success(View(row));
            var artifact = await store.GetArtifactAsync(row.ArtifactId, ct) ?? throw new InvalidOperationException("Deployment artifact disappeared.");
            var payload = Read<DeploymentArtifactPayload>(artifact.PayloadJson);
            var denied = await CheckRuntimeAsync(payload.Runtime ?? throw new InvalidOperationException("Runtime artifact missing."), actor, ct);
            if (denied is not null) return Fail<DeploymentReceipt>(denied);
            row.State = "Confirmed";
            row.Stage = "Active";
            row.ResourceId = row.Id;
            row.EvidenceJson = JsonSerializer.Serialize(new { artifact.PayloadHash, artifact.ArtifactHash, runtime = payload.Runtime.RuntimeVersion });
            row = await store.UpdateAsync(row, row.Version, ct, requireActionAuthority: true) ?? await RequireCurrentAsync(row.Id, ct);
            return AZOAResult<DeploymentReceipt>.Success(View(row));
        }
        var intent = request.NativeIntent ?? throw new InvalidOperationException("Native deployment intent missing.");
        var module = Module(request.Target);
        if (module is null) return Fail<DeploymentReceipt>("Native operation module unavailable.");
        if (row.PreparedJson is null)
        {
            if (!authorizeNewStage) return AZOAResult<DeploymentReceipt>.Success(View(row));
            var access = await valueAccess.RequireValueAccessAsync(actor, tenant, ct);
            if (access.IsError) return Fail<DeploymentReceipt>(access.Message);
            var allowed = await governance.EnsureChainAllowedAsync(intent.ChainType, "native-deployment", ct);
            if (!allowed.IsAllowed) return Fail<DeploymentReceipt>(allowed.ConsumerMessage);
            var history = Read<List<NativeStageHistory>>(row.HistoryJson);
            var next = await NextNativeStageAsync(module, intent, history, row, ct);
            if (next.IsError || next.Result is null) return Fail<DeploymentReceipt>(next.Message);
            if (next.Result.Complete)
            {
                if (history.Count == 0 || history.All(item => item.Observation.Verdict != "Confirmed")
                    || string.IsNullOrWhiteSpace(next.Result.ResourceId) || string.IsNullOrWhiteSpace(next.Result.EvidenceJson))
                    return Fail<DeploymentReceipt>("Native completion lacks admitted transaction and authoritative resource proof.");
                row.State = CompletionState(next.Result);
                row.Stage = "Complete";
                row.ResourceId = next.Result.ResourceId;
                row.EvidenceJson = next.Result.EvidenceJson;
                row = await store.UpdateAsync(row, row.Version, ct) ?? await RequireCurrentAsync(row.Id, ct);
                return AZOAResult<DeploymentReceipt>.Success(View(row));
            }
            if (string.IsNullOrWhiteSpace(next.Result.Stage)) return Fail<DeploymentReceipt>("Provider did not identify a preparation stage.");
            if (row.RecoveryMode != "None" && next.Result.Reservation is null)
                return Fail<DeploymentReceipt>("Recovery provider must disclose the exact selected-stage reserve before custody.");
            if (next.Result.Reservation is { } reserve && !WithinBudget(intent.Budget, history, reserve))
            {
                row.State = row.RecoveryMode == "None" ? "AwaitingAuthorization" : "RecoveryQuoteRequired";
                row.Detail = "The original cumulative budget cannot authorize this stage; a separately consented recovery quote is required.";
                row = await store.UpdateAsync(row, row.Version, ct) ?? await RequireCurrentAsync(row.Id, ct);
                return AZOAResult<DeploymentReceipt>.Success(View(row));
            }
            var signers = await BindNativeAuthorityAsync(intent, actor, tenant, row.ActionRunId, credential, ct);
            if (signers.IsError) return Fail<DeploymentReceipt>(signers.Message);
            row.Stage = next.Result.Stage;
            row.State = "Preparing";
            var preparing = await store.UpdateAsync(row, row.Version, ct, requireActionAuthority: true, signers.Result);
            if (preparing is null) return signers.Result is null ? AZOAResult<DeploymentReceipt>.Success(View(await RequireCurrentAsync(row.Id, ct)))
                : Fail<DeploymentReceipt>(AuthorityChanged);
            row = preparing;
            var prepared = await module.PrepareNativeStageAsync(intent, row.Stage, row.Generation, ct);
            if (prepared.IsError || prepared.Result is null)
            {
                row.State = "AwaitingAuthorization";
                row.Detail = prepared.Message;
                row = await store.UpdateAsync(row, row.Version, ct) ?? await RequireCurrentAsync(row.Id, ct);
                return AZOAResult<DeploymentReceipt>.Success(View(row));
            }
            if (prepared.Result.Stage != row.Stage || prepared.Result.ProtocolIdentity != intent.ProtocolIdentity
                || string.IsNullOrWhiteSpace(prepared.Result.TransactionId))
                return Fail<DeploymentReceipt>("Prepared stage violates its immutable identity.");
            if (!WithinBudget(intent.Budget, history, prepared.Result))
            {
                row.State = row.RecoveryMode == "None" ? "AwaitingAuthorization" : "RecoveryQuoteRequired";
                row.Detail = "The original cumulative budget cannot authorize this stage; a separately consented recovery quote is required.";
                row = await store.UpdateAsync(row, row.Version, ct) ?? await RequireCurrentAsync(row.Id, ct);
                return AZOAResult<DeploymentReceipt>.Success(View(row));
            }
            row.PreparedJson = JsonSerializer.Serialize(prepared.Result);
            row.SubmissionAuthorized = false;
            row.State = "Prepared";
            var persisted = await store.UpdateAsync(row, row.Version, ct);
            if (persisted is null) return AZOAResult<DeploymentReceipt>.Success(View(await RequireCurrentAsync(row.Id, ct)));
            row = persisted;
        }
        if (!row.SubmissionAuthorized)
        {
            if (!authorizeNewStage) return AZOAResult<DeploymentReceipt>.Success(View(row));
            var access = await valueAccess.RequireValueAccessAsync(actor, tenant, ct);
            if (access.IsError) return Fail<DeploymentReceipt>(access.Message);
            var allowed = await governance.EnsureChainAllowedAsync(intent.ChainType, "native-deployment", ct);
            if (!allowed.IsAllowed) return Fail<DeploymentReceipt>(allowed.ConsumerMessage);
            var signers = await BindNativeAuthorityAsync(intent, actor, tenant, row.ActionRunId, credential, ct);
            if (signers.IsError) return Fail<DeploymentReceipt>(signers.Message);
            row.SubmissionAuthorized = true;
            row.State = "Submitted";
            var authorized = await store.UpdateAsync(row, row.Version, ct, requireActionAuthority: true, signers.Result);
            if (authorized is null) return signers.Result is null ? AZOAResult<DeploymentReceipt>.Success(View(await RequireCurrentAsync(row.Id, ct)))
                : Fail<DeploymentReceipt>(AuthorityChanged);
            row = authorized;
        }
        var wire = Read<NativeStagePreparation>(row.PreparedJson!);
        var observed = await module.ObserveNativeStageAsync(intent, wire, ct);
        if (observed.IsError || observed.Result is null)
        {
            row.Detail = observed.Message;
            row.State = "NeedsOperator";
            row = await store.UpdateAsync(row, row.Version, ct) ?? await RequireCurrentAsync(row.Id, ct);
            return AZOAResult<DeploymentReceipt>.Success(View(row));
        }
        var observation = observed.Result;
        if (observation.TransactionId != wire.TransactionId || observation.ProtocolIdentity != wire.ProtocolIdentity)
            return Fail<DeploymentReceipt>("Provider observation does not match the saved transaction identity.");
        if (observation.Verdict is "Confirmed" or "FailedOnChain" or "ExpiredNoEffect")
        {
            var history = Read<List<NativeStageHistory>>(row.HistoryJson);
            history.Add(new NativeStageHistory(wire, observation));
            row.HistoryJson = JsonSerializer.Serialize(history);
            row.PreparedJson = null;
            row.SubmissionAuthorized = false;
            row.Detail = observation.Detail;
            row.State = observation.Verdict == "FailedOnChain" && row.RecoveryMode == "None" ? "Failed" : "AwaitingAuthorization";
            row.Generation++;
            if (observation.Verdict == "Confirmed")
            {
                var next = await NextNativeStageAsync(module, intent, history, row, ct);
                if (!next.IsError && next.Result is { Complete: true, ResourceId: not null, EvidenceJson: not null } completion)
                {
                    row.State = CompletionState(completion);
                    row.Stage = "Complete";
                    row.ResourceId = completion.ResourceId;
                    row.EvidenceJson = completion.EvidenceJson;
                }
            }
            row = await store.UpdateAsync(row, row.Version, ct) ?? await RequireCurrentAsync(row.Id, ct);
            return AZOAResult<DeploymentReceipt>.Success(View(row));
        }
        if (observation.Verdict == "Unknown")
        {
            row.State = "NeedsOperator";
            row.Detail = observation.Detail;
            row = await store.UpdateAsync(row, row.Version, ct) ?? await RequireCurrentAsync(row.Id, ct);
            return AZOAResult<DeploymentReceipt>.Success(View(row));
        }
        if (observation.Verdict != "Pending") return Fail<DeploymentReceipt>("Unknown provider observation verdict.");
        row.State = "Submitted";
        var submitted = await store.UpdateAsync(row, row.Version, ct);
        if (submitted is null) return AZOAResult<DeploymentReceipt>.Success(View(await RequireCurrentAsync(row.Id, ct)));
        var response = await module.SubmitNativeStageAsync(intent, wire, ct);
        if (!response.IsError && response.Result != wire.TransactionId)
            throw new InvalidOperationException("Provider submission returned another transaction identity.");
        if (response.IsError)
        {
            submitted.Detail = response.Message;
            submitted = await store.UpdateAsync(submitted, submitted.Version, ct) ?? await RequireCurrentAsync(submitted.Id, ct);
        }
        return AZOAResult<DeploymentReceipt>.Success(View(submitted));
    }

    private async Task<DeploymentOperation?> OwnedAsync(string id, Guid actor, Guid? tenant, CancellationToken ct)
    {
        var row = await store.GetAsync(id, ct);
        return row?.ActorUserId == actor.ToString("N") && row.TenantId == Tenant(tenant) ? row : null;
    }
    private async Task<DeploymentOperation> RequireCurrentAsync(string id, CancellationToken ct)
        => await store.GetAsync(id, ct) ?? throw new InvalidOperationException("Deployment admission disappeared.");
    private static bool WithinBudget(NativeBudget budget, IReadOnlyList<NativeStageHistory> history, NativeStagePreparation next)
        => WithinBudget(budget, history, new NativeStageReservation(next.ReservedFee, next.ReservedRent, next.ReservedValue));

    private static bool WithinBudget(NativeBudget budget, IReadOnlyList<NativeStageHistory> history, NativeStageReservation next)
    {
        if (!Positive(next.NetworkFee) || !Positive(next.LockedRent) || !Positive(next.Value)) return false;
        var fee = BigInteger.Parse(next.NetworkFee, CultureInfo.InvariantCulture);
        var rent = BigInteger.Parse(next.LockedRent, CultureInfo.InvariantCulture);
        var value = BigInteger.Parse(next.Value, CultureInfo.InvariantCulture);
        foreach (var item in history)
        {
            if (item.Observation.Verdict == "ExpiredNoEffect") continue;
            fee += BigInteger.Parse(item.Prepared.ReservedFee, CultureInfo.InvariantCulture);
            if (item.Observation.Verdict == "Confirmed")
            {
                rent += BigInteger.Parse(item.Prepared.ReservedRent, CultureInfo.InvariantCulture);
                value += BigInteger.Parse(item.Prepared.ReservedValue, CultureInfo.InvariantCulture);
            }
        }
        return fee <= BigInteger.Parse(budget.MaximumNetworkFee, CultureInfo.InvariantCulture)
            && rent <= BigInteger.Parse(budget.MaximumLockedRent, CultureInfo.InvariantCulture)
            && value <= BigInteger.Parse(budget.MaximumValue, CultureInfo.InvariantCulture);
    }
    private static DeploymentReceipt View(DeploymentOperation row)
    {
        var request = Read<DeploymentStartRequest>(row.RequestJson);
        var history = Read<List<NativeStageHistory>>(row.HistoryJson);
        var transactions = history.Select(item => item.Prepared.TransactionId).ToList();
        var prepared = row.PreparedJson is null ? null : Read<NativeStagePreparation>(row.PreparedJson);
        if (prepared is not null) transactions.Add(prepared.TransactionId);
        var accounting = history.Where(item => item.Observation.Accounting is not null).Select(item => item.Observation.Accounting!).ToArray();
        var paid = accounting.Aggregate(BigInteger.Zero, (sum, item) => sum + BigInteger.Parse(item.NetworkFeePaid, CultureInfo.InvariantCulture));
        var economics = request.NativeIntent is null ? null : new DeploymentEconomics(prepared?.ReservedFee ?? "0", prepared?.ReservedRent ?? "0",
            prepared?.ReservedValue ?? "0", paid.ToString(CultureInfo.InvariantCulture), accounting,
            prepared is null && history.All(item => item.Observation.Verdict == "ExpiredNoEffect" || item.Observation.Accounting is not null));
        return new(row.Id, row.ArtifactId, row.ArtifactHash, request.Target, row.State, row.Stage, row.ResourceId,
            request.Target.Kind == "PlatformRuntime" && row.State == "Confirmed" ? $"/api/deployments/{row.Id}/invoke" : null,
            transactions.Distinct(StringComparer.Ordinal).ToArray(), row.Detail, row.Version, economics,
            request.NativeIntent is { Quote: { } quote } intent ? new(intent.Kind, intent.Budget, quote, intent.Authorities, intent.Input) : null,
            NativeResourceEffects.FromHistory(history), NativeResourceEffects.CleanupFromHistory(history),
            row.RecoveryMode == "None" ? null : new(row.RecoveryMode, row.ParentOperationId, row.RecoveryChildId,
                row.RecoveryEvidenceJson, row.RecoveryProjected || row.RecoveryEvidenceJson is not null,
                row.ReplacesOperationId, row.ReplacedByOperationId));
    }
    private const string AuthorityChanged = "The deployment or its signing authority changed; reload the operation and continue.";
    private sealed record DiscoveryCursor(string Actor, string Tenant, DateTimeOffset Snapshot, int Offset);
}

