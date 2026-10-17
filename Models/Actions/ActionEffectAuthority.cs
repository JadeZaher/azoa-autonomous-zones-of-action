namespace AZOA.WebAPI.Models.Actions;

/// <summary>Immutable workflow authority carried internally into a durable domain effect.</summary>
public sealed record ActionEffectAuthority(Guid RunId, Guid NodeId, Guid ActorUserId, Guid? ActingTenantId, string IntentHash);
