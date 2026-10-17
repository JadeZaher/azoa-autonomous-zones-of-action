namespace AZOA.WebAPI.Models.Actions;

public sealed record ActionRecoveryRequest(Guid RequestId, long ExpectedVersion, string IntentHash, string Reason);

public sealed record ActionRecoveryView(Guid ExecutionId, Guid RunId, Guid NodeId, long Version,
    string State, string? RecoveryState, string? Domain, string? OperationId, string? IntentHash, bool CanRetry);

public sealed record ActionRecoveryReceipt(string AuditId, Guid ExecutionId, long ExpectedVersion, DateTimeOffset RequestedAt);
