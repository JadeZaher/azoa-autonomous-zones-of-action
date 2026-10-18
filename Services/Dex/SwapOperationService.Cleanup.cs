using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Services.Dex;

public sealed partial class SwapOperationService
{
    public async Task<AZOAResult<SwapOperationView>> BeginCleanupAsync(string operationId, SwapCleanupRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var semantics = request is null ? null
            : SwapCleanupContract.Semantics(request.AcceptCurrentZeroHoldingReplacement, request.AcceptCurrentAllowanceRevocation);
        if (request is null || semantics is null || request.ExpectedVersion < 0 || !ValidKey(request.RequestKey) ||
            request.AssetId is not { Length: > 0 and <= 256 } ||
            request.MaximumNetworkFee is not { Length: > 0 and <= 512 }) return Changed();
        var operation = await ReadOwnedAsync(operationId, credential, true, ct);
        if (operation is null) return Changed();
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var consent = new SwapCleanupConsent(1, operationId, operation.QuoteHash,
            CanonicalJson.Hash(new { Domain = "swap-cleanup-request/1", request.RequestKey }), credential.ActorUserId,
            credential.TenantId, intent.Terms.Owner, request.AssetId, semantics, request.MaximumNetworkFee,
            request.AcceptCurrentZeroHoldingReplacement, request.AcceptCurrentAllowanceRevocation);
        if (!SwapCleanupContract.IsValid(intent, consent)) return Fail("Cleanup consent differs from the original owner, asset, semantic or accepted cost ceiling.");
        var consentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(consent));
        if (operation.CleanupConsentJson is not null)
            return operation.CleanupConsentJson == consentJson ? AZOAResult<SwapOperationView>.Success(await ProjectAsync(operation, ct))
                : Fail("A different immutable cleanup command already owns this operation.");
        if (operation.Version != request.ExpectedVersion) return Changed();
        if (modules.ResolveForExisting(intent.Terms.Network) is not { } module || module.DexCapability.CleanupSemantics?.Contains(semantics) != true)
            return Fail("The original DEX module does not offer this cleanup semantic for this swap.");
        var authority = await authorities.BindDirectAsync(credential, consent.Owner.WalletId, intent.Terms.Network.ChainType,
            consent.Owner.Address, [AzoaScopes.SwapSign], ct);
        if (authority.IsError || authority.Result is null) return Fail(authority.Message);
        var saved = await operations.BeginCleanupAsync(operationId, request.ExpectedVersion, consent, authority.Result, ct);
        return saved is null ? Changed() : AZOAResult<SwapOperationView>.Success(await ProjectAsync(saved, ct));
    }

    public async Task<AZOAResult<SwapOperationView>> PrepareCleanupAsync(string operationId, SwapCleanupStageCommand command,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (command is null || command.ExpectedVersion < 0 || command.Stage is not { Length: > 0 and <= 64 }) return Changed();
        var operation = await ReadOwnedAsync(operationId, credential, true, ct);
        if (operation is null || operation.Version != command.ExpectedVersion || operation.CleanupConsentJson is null) return Changed();
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var consent = JsonSerializer.Deserialize<SwapCleanupConsent>(operation.CleanupConsentJson)!;
        if (consent.ActorUserId != credential.ActorUserId || consent.TenantId != credential.TenantId) return Changed();
        var module = modules.ResolveForExisting(intent.Terms.Network);
        if (module is null) return Fail("The original DEX module is unavailable for cleanup.");
        var authority = await authorities.BindDirectAsync(credential, consent.Owner.WalletId, intent.Terms.Network.ChainType,
            consent.Owner.Address, [AzoaScopes.SwapSign], ct);
        if (authority.IsError || authority.Result is null) return Fail(authority.Message);
        var result = await stages.PlanCleanupAsync(operationId, command.ExpectedVersion, authority.Result, module, command.Stage, ct);
        return result.IsError || result.Result is null ? Fail(result.Message) : await GetAsync(operationId, credential, ct);
    }
}
