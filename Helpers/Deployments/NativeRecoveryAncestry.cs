using System.Text.Json;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Helpers.Deployments;

/// <summary>Canonical immutable ancestry identity and bounded-page evidence construction.</summary>
public static class NativeRecoveryAncestry
{
    public const int PageSize = 25;
    public static string AuditId(string parentId, string headId) => CanonicalJson.Hash(new { Version = 1, Parent = parentId, Head = headId });
    public static string EntryId(string auditId, string operationId) => CanonicalJson.Hash(new { Audit = auditId, Operation = operationId });

    public static DeploymentRecoveryAudit Begin(DeploymentOperation parent, DeploymentOperation head)
    {
        if (head.ParentOperationId != parent.Id || parent.RecoveryChildId != head.Id
            || parent.ActorUserId != head.ActorUserId || parent.TenantId != head.TenantId
            || parent.RecoveryBindingHash != head.RecoveryBindingHash)
            throw new ArgumentException("Recovery ancestry head must belong to the owned parent.");
        var id = AuditId(parent.Id, head.Id);
        return new() { Id = id, ParentOperationId = parent.Id, HeadOperationId = head.Id,
            HeadIntentHash = head.IntentHash, HeadPredecessorId = head.ReplacesOperationId,
            ActorUserId = parent.ActorUserId, TenantId = parent.TenantId, NextOperationId = head.ReplacesOperationId,
            ExpectedSuccessorId = head.Id, Digest = CanonicalJson.Hash(new { Version = 1, Audit = id, HeadIntent = head.IntentHash }),
            State = head.ReplacesOperationId is null ? "Complete" : "Running", UpdatedAt = DateTimeOffset.UtcNow };
    }

    public static DeploymentRecoveryAuditEntry? Inspect(DeploymentRecoveryAudit cursor, DeploymentOperation row)
    {
        if (cursor.State != "Running" || row.Id != cursor.NextOperationId || row.Id == cursor.HeadOperationId
            || row.Id == cursor.ParentOperationId || row.ParentOperationId != cursor.ParentOperationId
            || row.ActorUserId != cursor.ActorUserId || row.TenantId != cursor.TenantId
            || row.ReplacedByOperationId != cursor.ExpectedSuccessorId || row.ReplacesOperationId == row.Id
            || NativeRecoveryReplacement.ProofKind(row, inspectSealed: true) is null) return null;
        string requestHash, historyHash;
        try
        {
            requestHash = CanonicalJson.Hash(JsonSerializer.Deserialize<JsonElement>(row.RequestJson));
            historyHash = CanonicalJson.Hash(JsonSerializer.Deserialize<JsonElement>(row.HistoryJson));
        }
        catch (JsonException) { return null; }
        var entry = new DeploymentRecoveryAuditEntry { Id = EntryId(cursor.Id, row.Id), AuditId = cursor.Id,
            OperationId = row.Id, SuccessorId = cursor.ExpectedSuccessorId, PredecessorId = row.ReplacesOperationId,
            IntentHash = row.IntentHash, RequestHash = requestHash, HistoryHash = historyHash,
            OperationVersion = row.Version, Position = checked(cursor.InspectedCount + 1) };
        entry.Digest = CanonicalJson.Hash(new { Previous = cursor.Digest, entry.OperationId, entry.SuccessorId,
            entry.PredecessorId, entry.IntentHash, entry.RequestHash, entry.HistoryHash, entry.OperationVersion, entry.Position });
        return entry;
    }

    public static void Advance(DeploymentRecoveryAudit cursor, DeploymentRecoveryAuditEntry entry)
    {
        cursor.NextOperationId = entry.PredecessorId; cursor.ExpectedSuccessorId = entry.OperationId;
        cursor.Digest = entry.Digest; cursor.InspectedCount = entry.Position;
        cursor.State = entry.PredecessorId is null ? "Complete" : "Running";
    }
}
