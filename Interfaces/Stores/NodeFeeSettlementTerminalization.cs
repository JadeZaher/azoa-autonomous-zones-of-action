namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Validated chain-observation proof required to complete a paired settlement.</summary>
public sealed record NodeFeeSettlementTerminalization(
    string ParentIdempotencyKey,
    string PrimaryEffectReference,
    string FeeEffectReference,
    string ParentResultPayload);
