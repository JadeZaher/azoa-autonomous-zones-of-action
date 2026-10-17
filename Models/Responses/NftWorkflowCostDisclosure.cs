namespace AZOA.WebAPI.Models.Responses;

/// <summary>Cost offer for one exact persisted workflow execution version.</summary>
public sealed record NftWorkflowCostDisclosure(Guid ExecutionId, long ExecutionVersion, NftProtocolCostOffer Offer);

/// <summary>Evidence-based outcome for a saved workflow acceptance request.</summary>
public sealed record NftWorkflowCostResolution(string State, NftLifecycleView? Lifecycle = null);
