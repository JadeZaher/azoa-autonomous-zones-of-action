using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Hold;

public sealed partial class HoldCommandService
{
    /// <summary>Discloses a new quote only after exact no-effect proof or before original cost consent.</summary>
    public async Task<AZOAResult<HoldCommandView>> ReplaceAsync(string id, HoldRecoveryRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (request.RequestId == Guid.Empty || request.Version < 0 || string.IsNullOrWhiteSpace(request.Reason)
            || request.Reason.Length > 500) return Refuse();
        var receiptId = HoldRecoveryIdentity.Id("Replace", id, credential.ActorUserId, credential.TenantId, request.RequestId);
        var prior = await store.GetRecoveryAsync(receiptId, ct);
        if (prior is not null)
            return prior.Kind == "Replace" && prior.OperationId == id
                && prior.RequestHash == HoldRecoveryIdentity.RequestHash("Replace", id, credential.ActorUserId, credential.TenantId, request)
                ? Result(JsonSerializer.Deserialize<HoldCommand>(prior.OutcomeJson)!) : Refuse();
        var row = await store.GetCommandAsync(id, ct);
        if (row?.RailPlanJson is null || row.ActorUserId != credential.ActorUserId.ToString("N")
            || row.TenantId != credential.TenantId?.ToString("N") || row.Version != request.Version
            || row.Generation == long.MaxValue || (row.State is not ("FailedNoEffect" or "AwaitingConsent")
                && !(row.State == "Admitted" && row.AcceptanceState == "FailedNoEffect"))) return Refuse();
        var access = await valueAccess.RequireValueAccessAsync(credential.ActorUserId, credential.TenantId, ct);
        if (access.IsError) return AZOAResult<HoldCommandView>.Failure(access.Message);
        var original = JsonSerializer.Deserialize<HoldRailPlan>(row.RailPlanJson)!;
        var consent = await authorities.BindConsentAsync(credential, [AzoaScopes.TransferSign], ct);
        if (consent.IsError || consent.Result is null) return AZOAResult<HoldCommandView>.Failure(consent.Message);
        SigningAdmissionAuthority? sender = null;
        if (original.SenderWallet is { } wallet)
        {
            var bound = await authorities.BindDirectAsync(credential, wallet.Id, wallet.ChainType, wallet.Address, [AzoaScopes.TransferSign], ct);
            if (bound.IsError || bound.Result is null) return AZOAResult<HoldCommandView>.Failure(bound.Message);
            sender = bound.Result;
        }
        var provider = providers.GetProvider(original.Quote.Request.ChainType, original.Quote.Request.Network);
        if (!provider.TryGetModule<IValueCustodyModule>(out var module))
            return AZOAResult<HoldCommandView>.Failure("The original custody provider cannot quote replacement costs.");
        var quote = await module!.QuoteValueCustodyAsync(original.Quote.Request, ct);
        if (quote.IsError || quote.Result is null) return AZOAResult<HoldCommandView>.Failure(quote.Message);
        var replacement = original with { Generation = row.Generation + 1, Quote = quote.Result };
        var saved = await store.ReplaceRailAsync(row, replacement, new(1, Direct: consent.Result, Sender: sender), request, ct);
        return saved is null ? AZOAResult<HoldCommandView>.Failure("The original no-effect evidence, quote or recovery authority changed.")
            : Result(saved);
    }
}
