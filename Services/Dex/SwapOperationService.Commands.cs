using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Dex;

public sealed partial class SwapOperationService
{
    public async Task<AZOAResult<SwapOperationView>> PrepareAsync(string operationId, long expectedVersion,
        SigningCredentialWitness credential, WorkflowSigningWitness? workflow = null, CancellationToken ct = default)
    {
        var operation = await ReadOwnedAsync(operationId, credential, false, ct);
        if (operation is null || operation.Version != expectedVersion || !SameWorkflow(operation, workflow)) return Changed();
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var module = modules.Resolve(intent.Terms.Network);
        if (module is null) return Fail("The original DEX module is unavailable for this network.");
        var authority = await BindAsync(credential, intent.Terms, workflow, ct);
        if (authority.IsError || authority.Result is null) return Fail(authority.Message);
        var progressed = await stages.ProgressAsync(operationId, expectedVersion, authority.Result, module, ct);
        return progressed.IsError || progressed.Result is null ? Refusal(progressed.Message, progressed.Code)
            : AZOAResult<SwapOperationView>.Success(await ProjectAsync(progressed.Result, ct));
    }

    public async Task<AZOAResult<SwapOperationView>> SignWithCustodyAsync(string operationId, SwapStageCommand command,
        SigningCredentialWitness credential, WorkflowSigningWitness? workflow = null, CancellationToken ct = default)
    {
        var selected = await CommandAsync(operationId, command, credential, ct);
        if (selected is null) return Changed();
        var (operation, stage) = selected.Value;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var module = modules.Resolve(intent.Terms.Network);
        if (module is null) return Fail("The original DEX module is unavailable for this network.");
        var authority = await StageAuthorityAsync(operation, stage, credential, workflow, ct);
        if (authority.IsError || authority.Result is null) return Fail(authority.Message);
        var signed = await stages.SignAsync(operationId, command.ExpectedVersion, command.StageId, command.ExpectedStageVersion,
            authority.Result, module, ct);
        return signed.IsError || signed.Result is null ? Refusal(signed.Message, signed.Code) : await GetAsync(operationId, credential, ct);
    }

    public async Task<AZOAResult<SwapOperationView>> SubmitAsync(string operationId, SwapStageCommand command,
        SigningCredentialWitness credential, WorkflowSigningWitness? workflow = null, CancellationToken ct = default)
    {
        var selected = await CommandAsync(operationId, command, credential, ct);
        if (selected is null) return Changed();
        var (operation, stage) = selected.Value;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var module = modules.Resolve(intent.Terms.Network);
        if (module is null) return Fail("The original DEX module is unavailable for this network.");
        var authority = await StageAuthorityAsync(operation, stage, credential, workflow, ct);
        if (authority.IsError || authority.Result is null) return Fail(authority.Message);
        var authorized = await operations.AuthorizeStageAsync(operationId, command.ExpectedVersion, command.StageId,
            command.ExpectedStageVersion, authority.Result, ct);
        if (authorized is null) return Changed();
        await recovery.ReconcileAsync(operationId, ct);
        return await GetAsync(operationId, credential, ct);
    }

    public async Task<AZOAResult<SwapOperationView>> RefreshAsync(string operationId, SigningCredentialWitness credential,
        CancellationToken ct = default)
    {
        var operation = await ReadOwnedAsync(operationId, credential, true, ct);
        if (operation is null) return Changed();
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var module = modules.Resolve(intent.Terms.Network);
        if (module is null) return Fail("The original DEX module is unavailable for reconciliation.");
        await recovery.ReconcileAsync(operationId, ct);
        return await GetAsync(operationId, credential, ct);
    }

    public async Task<AZOAResult<SwapOperationView>> CancelUnsentAsync(string operationId, long expectedVersion,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var operation = await ReadOwnedAsync(operationId, credential, true, ct);
        if (operation is null) return Changed();
        if (operation.State == "Cancelled" || operation.Version != expectedVersion)
        {
            // A retry after a crash between the cancel commit and the hand-back re-runs the idempotent release.
            await ReleaseCancelledStageAsync(operationId, operation, ct);
            return operation.State == "Cancelled" ? AZOAResult<SwapOperationView>.Success(await ProjectAsync(operation, ct)) : Changed();
        }
        var consent = await authorities.BindConsentAsync(credential, [AzoaScopes.SwapSign], ct);
        if (consent.IsError || consent.Result is null) return Fail(consent.Message);
        if (operation.CurrentStageId is null)
        {
            if (operation.State != "Admitted") return Changed();
            if (await operations.CancelBeforePreparationAsync(operation, consent.Result, ct) is null) return Changed();
        }
        else
        {
            var stage = await operations.GetStageAsync(operation.CurrentStageId, ct);
            if (stage is null || await operations.CancelUnsentStageAsync(operationId, expectedVersion, operation.CurrentStageId,
                stage.Version, consent.Result, ct) is null) return Changed();
            await ReleaseCancelledStageAsync(operationId, operation, ct);
        }
        return await GetAsync(operationId, credential, ct);
    }

    /// <summary>Hands an unsent cancelled current stage's provider reservation back; a no-op for any other stage.</summary>
    private async Task ReleaseCancelledStageAsync(string operationId, SwapOperationRecord operation, CancellationToken ct)
    {
        if (operation.CurrentStageId is not { } stageId) return;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        if (modules.Resolve(intent.Terms.Network) is { } module)
            await stages.ReleaseUnsentStageAsync(operationId, stageId, module, ct);
    }

    private async Task<(SwapOperationRecord Operation, SwapStageRecord Stage)?> CommandAsync(string operationId,
        SwapStageCommand command, SigningCredentialWitness credential, CancellationToken ct)
    {
        if (command is null || command.ExpectedVersion < 0 || command.ExpectedStageVersion < 0 || command.StageId is not { Length: 64 }) return null;
        var operation = await ReadOwnedAsync(operationId, credential, true, ct);
        if (operation is null || operation.Version != command.ExpectedVersion || operation.CurrentStageId != command.StageId) return null;
        var stage = await operations.GetStageAsync(command.StageId, ct);
        return stage is null || stage.OperationId != operationId || stage.IntentHash != operation.IntentHash || stage.Version != command.ExpectedStageVersion
            ? null : (operation, stage);
    }

    private Task<AZOAResult<SigningAdmissionAuthority>> StageAuthorityAsync(SwapOperationRecord operation, SwapStageRecord stage,
        SigningCredentialWitness credential, WorkflowSigningWitness? workflow, CancellationToken ct)
    {
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var plan = JsonSerializer.Deserialize<SwapStagePlan>(stage.PlanJson)!;
        if (plan.Purpose == "Cleanup" && plan.CleanupConsent is { } consent && workflow is null &&
            consent.ActorUserId == credential.ActorUserId && consent.TenantId == credential.TenantId)
            return authorities.BindDirectAsync(credential, consent.Owner.WalletId, intent.Terms.Network.ChainType,
                consent.Owner.Address, [AzoaScopes.SwapSign], ct);
        return SameWorkflow(operation, workflow) ? BindAsync(credential, intent.Terms, workflow, ct)
            : Task.FromResult(AZOAResult<SigningAdmissionAuthority>.Failure("The original workflow signing authority is required."));
    }

    /// <summary>Keeps the typed unsettleable outcome visible to workflow and client callers; other refusals stay conflicts.</summary>
    private static AZOAResult<SwapOperationView> Refusal(string message, string? code)
        => code == DexResultCodes.Unsettleable ? AZOAResult<SwapOperationView>.FailureWithCode(message, code) : Fail(message);

    private static AZOAResult<SwapOperationView> Changed() => Fail("The operation, selected stage, version or participant scope changed; reload its current state.");
}
