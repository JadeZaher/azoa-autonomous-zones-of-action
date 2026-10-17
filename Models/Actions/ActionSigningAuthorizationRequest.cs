namespace AZOA.WebAPI.Models.Actions;

/// <summary>Authorizes only an exact existing frozen node; it cannot select a replacement domain or intent.</summary>
public sealed record ActionSigningAuthorizationRequest(Guid RequestId, long ExpectedVersion, string IntentHash, string Reason);

/// <summary>Public receipt omitting private credential and grant witnesses.</summary>
public sealed record ActionSigningAuthorizationReceipt(Guid AuthorizationId, Guid RunId, Guid NodeId, string IntentHash);
