using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Helpers;

/// <summary>Checks immutable action correlation and its one-time domain identity attachment.</summary>
public static class ActionOperationIdentity
{
    public static bool Matches(ActionExternalOperationReference saved, ActionExternalOperationReference next) =>
        saved.Version == next.Version && saved.Domain == next.Domain && saved.RequestKey == next.RequestKey
        && saved.IntentHash == next.IntentHash && saved.ActorUserId == next.ActorUserId && saved.ActingTenantId == next.ActingTenantId
        && (saved.OperationId is null || saved.OperationId == next.OperationId);

    public static bool CanUpdate(ActionNodeExecution saved, ActionNodeExecution next) =>
        saved.Id == next.Id && saved.RunId == next.RunId && saved.NodeId == next.NodeId
        && (saved.WaitUntil is null || saved.WaitUntil == next.WaitUntil)
        && (!saved.ManualAdvanceReleased || next.ManualAdvanceReleased)
        && (saved.FrozenInput is null || saved.FrozenInput == next.FrozenInput)
        && (saved.SigningAuthorityJson is null || saved.SigningAuthorityJson == next.SigningAuthorityJson)
        && (saved.AdmissionRejection is null || saved.AdmissionRejection == next.AdmissionRejection)
        && (saved.ExternalOperation is null || (next.ExternalOperation is not null && Matches(saved.ExternalOperation, next.ExternalOperation)));
}
