namespace AZOA.WebAPI.Models.HostedSessions;

/// <summary>Distinguishes opaque invalid callbacks from authenticated work requiring delivery retry.</summary>
public enum HostedIdentityCallbackDisposition { Ignored, Processed, Retry }
