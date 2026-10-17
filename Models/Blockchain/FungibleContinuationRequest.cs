namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>Fresh participant approval to continue one exact preserved operation version.</summary>
public sealed record FungibleContinuationRequest(Guid RequestId, long Version);
