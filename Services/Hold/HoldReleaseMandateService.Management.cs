using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Hold;

public sealed partial class HoldReleaseMandateService
{
    public async Task<AZOAResult<HoldMandateRecoveryResolution>> ReconcileRecoveryAsync(string id, HoldMandateRecoveryLookup lookup,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (lookup?.Request is null || lookup.Kind is not ("MandateRevoke" or "MandateReauthorize")
            || lookup.Request.RequestId == Guid.Empty || lookup.Request.Version < 0)
            return AZOAResult<HoldMandateRecoveryResolution>.Failure("The exact saved management request is required.");
        var receipt = await mandates.GetRecoveryAsync(id, lookup.Kind, lookup.Request, credential, ct);
        if (receipt is not null) return AZOAResult<HoldMandateRecoveryResolution>.Success(new("Applied", View(receipt)));
        var row = await mandates.GetAsync(id, ct);
        if (row is null || row.ActorUserId != credential.ActorUserId.ToString("N")
            || (lookup.Kind == "MandateReauthorize" && row.TenantId != credential.TenantId?.ToString("N"))
            || (lookup.Kind == "MandateRevoke" && (!credential.OwnerAuthority || credential.TenantId is not null)))
            return AZOAResult<HoldMandateRecoveryResolution>.Failure("Release mandate not found.");
        if (row.Version <= lookup.Request.Version)
            return AZOAResult<HoldMandateRecoveryResolution>.Success(new("Unresolved", View(row)));
        receipt = await mandates.GetRecoveryAsync(id, lookup.Kind, lookup.Request, credential, ct);
        return AZOAResult<HoldMandateRecoveryResolution>.Success(receipt is null
            ? new("Obsolete", View(row)) : new("Applied", View(receipt)));
    }

    public async Task<AZOAResult<HoldReleaseMandateView>> GetAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        var row = await mandates.GetAsync(id, ct);
        return row is not null && row.ActorUserId == actor.ToString("N") && row.TenantId == tenant?.ToString("N")
            ? AZOAResult<HoldReleaseMandateView>.Success(View(row)) : AZOAResult<HoldReleaseMandateView>.Failure("Release mandate not found.");
    }

    public async Task<AZOAResult<HoldReleaseMandatePage>> ListAsync(Guid actor, Guid? tenant, int offset, int limit, CancellationToken ct = default)
    {
        if (actor == Guid.Empty || tenant == Guid.Empty || offset < 0 || offset > int.MaxValue - 100 || limit is < 1 or > 100)
            return AZOAResult<HoldReleaseMandatePage>.Failure("A valid owner scope and bounded page are required.");
        var rows = await mandates.ListOwnerAsync(actor, tenant, offset, limit, ct);
        return AZOAResult<HoldReleaseMandatePage>.Success(new(rows.Select(View).ToArray(), rows.Count == limit ? offset + rows.Count : null));
    }

    public async Task<AZOAResult<HoldReleaseMandateView>> ReauthorizeAsync(string id, HoldRecoveryRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var replay = await mandates.GetRecoveryAsync(id, "MandateReauthorize", request, credential, ct);
        if (replay is not null) return AZOAResult<HoldReleaseMandateView>.Success(View(replay));
        var authority = await authorities.BindConsentAsync(credential, [AzoaScopes.TransferSign], ct);
        if (authority.IsError || authority.Result is null) return AZOAResult<HoldReleaseMandateView>.Failure(authority.Message);
        var row = await mandates.ReauthorizeAsync(id, request, authority.Result, ct);
        return row is null ? AZOAResult<HoldReleaseMandateView>.Failure("The active original mandate or current authority changed.")
            : AZOAResult<HoldReleaseMandateView>.Success(View(row));
    }

    public async Task<AZOAResult<HoldReleaseMandateView>> RevokeAsync(string id, HoldRecoveryRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var row = await mandates.RevokeAsync(id, request, credential, ct);
        return row is null ? AZOAResult<HoldReleaseMandateView>.Failure("The original mandate, exact request version or owner authority changed.")
            : AZOAResult<HoldReleaseMandateView>.Success(View(row));
    }

    private static HoldReleaseMandateView View(HoldReleaseMandate row)
        => new(row.Id, row.Version, row.Status, JsonSerializer.Deserialize<HoldReleaseMandateTerms>(row.TermsJson)!, row.OperationId, row.Detail);
}
