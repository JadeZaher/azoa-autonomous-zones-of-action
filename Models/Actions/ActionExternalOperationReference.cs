namespace AZOA.WebAPI.Models.Actions;

/// <summary>Immutable correlation for one admitted domain operation.</summary>
public sealed record ActionExternalOperationReference(
    int Version,
    string Domain,
    string RequestKey,
    string? OperationId,
    string IntentHash,
    Guid ActorUserId,
    Guid? ActingTenantId);

/// <summary>Domain-authoritative recovery outcome for a saved action operation.</summary>
public enum ActionExternalOperationStatus { Pending, Succeeded, Failed, Cancelled, Ambiguous }

/// <summary>Observation result, never authority for a fresh participant signature.</summary>
public sealed record ActionExternalOperationObservation(
    ActionExternalOperationStatus Status,
    ActionExternalOperationReference Reference,
    string? Output = null,
    string? Detail = null,
    string? TransactionId = null,
    string? ChainType = null);
