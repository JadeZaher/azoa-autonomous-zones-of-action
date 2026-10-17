using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Services.Dex;

public sealed partial class SwapOperationService
{
    public async Task<AZOAResult<SwapExternalSigningView>> ExposeAsync(string operationId, SwapExposureCommand command,
        SigningCredentialWitness credential, WorkflowSigningWitness? workflow = null, CancellationToken ct = default)
    {
        if (command is null || !command.AcceptPotentialExternalSubmission || command.StageId is not { Length: 64 }) return Refused();
        var operation = await ReadOwnedAsync(operationId, credential, true, ct);
        if (operation?.CurrentStageId != command.StageId) return Refused();
        var stage = await operations.GetStageAsync(command.StageId, ct);
        if (stage is null || stage.OperationId != operationId || stage.IntentHash != operation.IntentHash ||
            CanonicalJson.HashCanonical(stage.PlanJson) != stage.PlanHash) return Refused();
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var plan = JsonSerializer.Deserialize<SwapStagePlan>(stage.PlanJson)!;
        var tenant = plan.Purpose == "Cleanup" ? plan.CleanupConsent?.TenantId : intent.Terms.TenantId;
        if (credential.ActorUserId != intent.Terms.ActorUserId || credential.TenantId != tenant) return Refused();
        if (stage.ExternalExposedAt is null)
        {
            var module = modules.Resolve(intent.Terms.Network);
            if (module is null) return Refused();
            var authority = await StageAuthorityAsync(operation, stage, credential, workflow, ct);
            if (authority.IsError || authority.Result is null) return Refused();
            var exposed = await stages.ExposeAsync(operationId, command.ExpectedVersion, command.StageId,
                command.ExpectedStageVersion, true, authority.Result, module, ct);
            if (exposed.IsError || exposed.Result is null) return Refused();
            stage = exposed.Result;
            operation = (await operations.GetAsync(operationId, ct))!;
        }
        // A replayed disclosure still needs the caller's live swap-signing consent, never only the original exposure record.
        else if ((await authorities.BindConsentAsync(credential, [AzoaScopes.SwapSign], ct)) is { IsError: true } or { Result: null })
            return Refused();
        if (stage.ExternalExposureHash is null) return Refused();
        return AZOAResult<SwapExternalSigningView>.Success(new(operationId, command.StageId, operation.Version, stage.Version,
            intent.Terms.Network, stage.PlanHash, stage.ExternalExposureHash, plan.RequiredSignerAddresses,
            plan.UnsignedPayloadJson, plan.ReservedNetworkFee, plan.ReservedLockedBalance));

        static AZOAResult<SwapExternalSigningView> Refused() => AZOAResult<SwapExternalSigningView>.FailureWithCode(
            "Explicit potential-submission consent, original participant and current stage authority are required.", "Conflict");
    }

    public async Task<AZOAResult<SwapOperationView>> AcceptBrowserAsync(string operationId, SwapBrowserStageCommand command,
        SigningCredentialWitness credential, WorkflowSigningWitness? workflow = null, CancellationToken ct = default)
    {
        if (command is null || command.SignedPayloadJson is not { Length: > 0 and <= 1048576 }) return Changed();
        var selected = await CommandAsync(operationId, new(command.ExpectedVersion, command.StageId, command.ExpectedStageVersion), credential, ct);
        if (selected is null) return Changed();
        var (operation, stage) = selected.Value;
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var module = modules.Resolve(intent.Terms.Network);
        if (module is null || stage.ExternalExposedAt is null) return Changed();
        var authority = await StageAuthorityAsync(operation, stage, credential, workflow, ct);
        if (authority.IsError || authority.Result is null) return Fail(authority.Message);
        var saved = await stages.AcceptBrowserAsync(operationId, command.ExpectedVersion, command.StageId, command.ExpectedStageVersion,
            command.SignedPayloadJson, authority.Result, module, ct);
        return saved.IsError || saved.Result is null ? Fail(saved.Message) : await GetAsync(operationId, credential, ct);
    }

    public async Task<AZOAResult<SwapOperationView>> WithdrawSubmissionAsync(string operationId, SwapStageCommand command,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var operation = await ReadOwnedAsync(operationId, credential, true, ct);
        if (command is null || operation?.CurrentStageId != command.StageId) return Changed();
        var consent = await authorities.BindConsentAsync(credential, [AzoaScopes.SwapSign], ct);
        if (consent.IsError || consent.Result is null) return Fail(consent.Message);
        var saved = await operations.WithdrawSubmissionAsync(operationId, command.ExpectedVersion, command.StageId,
            command.ExpectedStageVersion, consent.Result, ct);
        return saved is null ? Changed() : await GetAsync(operationId, credential, ct);
    }
}
