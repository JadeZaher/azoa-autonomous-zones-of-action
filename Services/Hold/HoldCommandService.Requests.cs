using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Hold;

public sealed partial class HoldCommandService
{
    /// <summary>Resolves original participant requests without obtaining consent or advancing custody.</summary>
    public async Task<AZOAResult<HoldCommandRequestResolution>> ReconcileRequestAsync(string id, HoldCommandRequestLookup lookup,
        Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        if (lookup is null) return Invalid();
        var consent = lookup.Kind is "Transfer" or "Acceptance";
        var recovery = lookup.Kind is "Replace" or "Cancel";
        if (actor == Guid.Empty || tenant == Guid.Empty || (!consent && !recovery)
            || (consent && (lookup.Consent is null || lookup.Recovery is not null))
            || (recovery && (lookup.Recovery is null || lookup.Consent is not null))) return Invalid();
        var requestId = consent ? lookup.Consent!.RequestId : lookup.Recovery!.RequestId;
        var version = consent ? lookup.Consent!.Version : lookup.Recovery!.Version;
        if (requestId == Guid.Empty || version < 0 || version == long.MaxValue) return Invalid();
        var row = await store.GetCommandAsync(id, ct);
        if (!Visible(row, actor, tenant) && !(lookup.Kind == "Cancel" && tenant is null && row?.ActorUserId == actor.ToString("N"))) return Invalid();
        bool applied;
        if (consent)
        {
            var audit = await store.GetAuthorizationAsync(HoldConsentIdentity.Id(lookup.Kind, id, actor, tenant, requestId), ct);
            if (audit is not null && (audit.Kind != lookup.Kind || audit.OperationId != id || audit.ActorUserId != actor.ToString("N")
                || audit.TenantId != tenant?.ToString("N") || audit.RequestHash != HoldConsentIdentity.RequestHash(lookup.Kind, id, actor, tenant, lookup.Consent!)))
                return Invalid();
            applied = audit is not null;
        }
        else
        {
            var audit = await store.GetRecoveryAsync(HoldRecoveryIdentity.Id(lookup.Kind, id, actor, tenant, requestId), ct);
            if (audit is not null && (audit.Kind != lookup.Kind || audit.OperationId != id
                || audit.ActorUserId != actor.ToString("N") || audit.TenantId != tenant?.ToString("N")
                || audit.RequestHash != HoldRecoveryIdentity.RequestHash(lookup.Kind, id, actor, tenant, lookup.Recovery!))) return Invalid();
            applied = audit is not null;
        }
        var result = await ReadResultAsync(row!, ct);
        return result.IsError || result.Result is null ? AZOAResult<HoldCommandRequestResolution>.Failure(result.Message)
            : AZOAResult<HoldCommandRequestResolution>.Success(new(applied ? "Applied" : row!.Version > version ? "Obsolete" : "Unresolved", result.Result));
    }

    private static AZOAResult<HoldCommandRequestResolution> Invalid()
        => AZOAResult<HoldCommandRequestResolution>.Failure("The exact original scoped command request is required.");
}
