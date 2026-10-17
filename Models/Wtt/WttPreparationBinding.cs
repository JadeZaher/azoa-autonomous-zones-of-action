namespace AZOA.WebAPI.Models.Wtt;

/// <summary>Frozen owner, quote and intent for one protocol stage generation.</summary>
public sealed record WttPreparationBinding(string ProtocolIdentity, Guid ActorUserId, Guid? TenantId,
    string OperationId, string Phase, string Stage, int Generation, string IntentHash, string QuoteId);
