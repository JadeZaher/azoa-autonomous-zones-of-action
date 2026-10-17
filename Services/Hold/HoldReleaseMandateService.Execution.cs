using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Hold;

public sealed partial class HoldReleaseMandateService
{
    /// <summary>Executes only the exact persisted release mandate; participant acceptance remains a separate authorized journey.</summary>
    public async Task<AZOAResult<HoldCommand>> AdvanceAcceptedAsync(string id, CancellationToken ct = default)
    {
        var mandate = await mandates.GetAsync(id, ct);
        if (mandate is null) return AZOAResult<HoldCommand>.Failure("The original release mandate is unavailable.");
        if (mandate.OperationId is { } operationId)
        {
            var original = await holds.GetCommandAsync(operationId, ct);
            if (original is null || JsonSerializer.Deserialize<HoldCommandAuthority>(original.AuthorityJson)?.ReleaseMandateId != id)
                return AZOAResult<HoldCommand>.Failure("The original mandate operation evidence is unavailable.");
            return await rail.AdvanceAsync(original, mandate.Status == "Active", ct);
        }
        if (mandate.Status != "Active" || mandate.NotBefore > DateTimeOffset.UtcNow || mandate.ExpiresAt <= DateTimeOffset.UtcNow)
            return AZOAResult<HoldCommand>.Failure("The release mandate is not currently executable.");
        var terms = JsonSerializer.Deserialize<HoldReleaseMandateTerms>(mandate.TermsJson)!;
        var hold = await holds.GetHoldAsync(mandate.HoldRecordId, ct);
        if (hold is null || hold.DefinitionJson is null || hold.ActiveReleaseMandateId != id || hold.State != "Held"
            || hold.DefinitionHash != terms.DefinitionHash || hold.ActiveCommandId is not null)
            return AZOAResult<HoldCommand>.Failure("The original reserved liability changed.");
        var provider = providers.GetProvider(terms.Asset.ChainType, terms.Asset.Network);
        if (!provider.TryGetModule<IValueCustodyModule>(out var custody))
            return AZOAResult<HoldCommand>.Failure("Complete custody costs are unavailable for this mandate.");
        var quote = await custody!.QuoteValueCustodyAsync(new(hold.Chain, terms.Asset.Network, hold.AssetId,
            hold.HoldingAddress, terms.Recipient.Address, hold.Amount, hold.HoldingAddress), ct);
        if (quote.IsError || quote.Result is null) return AZOAResult<HoldCommand>.Failure(quote.Message);
        if (!HoldReleaseMandateIdentity.CostsWithin(quote.Result, terms.MaximumCosts))
            return AZOAResult<HoldCommand>.Failure("Current custody costs exceed the explicitly accepted mandate categories.");
        var intent = new HoldCommandIntent(1, "mandate:" + id, "Release", hold.HoldId,
            Guid.Parse(mandate.ActorUserId), SurrealRecordGuid.Parse(hold.OwnerUserId!),
            SurrealRecordGuid.ParseOptional(mandate.TenantId), SurrealRecordGuid.ParseOptional(hold.TenantId), terms.Asset);
        var plan = new HoldRailPlan(1, "Release", HoldCommandIdentity.Operation(intent), 0, quote.Result, null, terms.Recipient,
            SigningContext.PlatformFor(hold.Chain) with { Scope = AzoaScopes.TransferSign });
        var admitted = await holds.ClaimRailAsync(intent, plan, new(1, ReleaseMandateId: id), ct);
        return admitted is null ? AZOAResult<HoldCommand>.Failure("The exact current mandate, costs or custody liability changed.")
            : await rail.AdvanceAsync(admitted, true, ct);
    }
}
