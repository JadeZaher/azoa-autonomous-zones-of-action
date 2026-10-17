using System.Text.Json;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

public partial class BlockchainOperationManager
{
    private async Task<AZOAResult<IBlockchainOperation>> ExecuteNativeAsync(IBlockchainOperation operation, AZOARequest? request)
    {
        if (_deployments is null) throw new InvalidOperationException("Deployment service registration is required.");
        if (request?.ActionAuthority is not null)
            return AZOAResult<IBlockchainOperation>.Failure("Action native effects require the frozen deployment node admission contract.", operation);
        var actor = operation.InitiatorUserId ?? operation.UserId;
        var key = request?.IdempotencyKey ?? operation.Parameters.GetValueOrDefault("DeploymentRequestKey") ?? operation.IdempotencyKey;
        if (actor is null || actor == Guid.Empty || operation.UserId != actor || string.IsNullOrWhiteSpace(key)
            || !operation.Parameters.TryGetValue("DeploymentRequest", out var json))
            return AZOAResult<IBlockchainOperation>.Failure("Native operations require their caller, request key, and typed DeploymentRequest.", operation);
        DeploymentStartRequest? start;
        try { start = JsonSerializer.Deserialize<DeploymentStartRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return AZOAResult<IBlockchainOperation>.Failure("DeploymentRequest is invalid JSON.", operation); }
        var expectedKind = operation.OperationType == "DeployContract" ? "Deploy" : "Call";
        if (start?.Target?.Kind != "NativeContract" || start.NativeIntent?.Kind != expectedKind
            || start.NativeIntent.Authorities is not { Count: > 0 }
            || start.NativeIntent.Authorities.Any(x => x is null || !x.SigningContext.IsResolvableUserContext
                || x.SigningContext.UserId != actor || x.SigningContext.ActingTenantId != (operation.ActingTenantId ?? Guid.Empty))
            || (operation.WalletId.HasValue && !start.NativeIntent.Authorities.Any(x => x.SigningContext.WalletId == operation.WalletId)))
            return AZOAResult<IBlockchainOperation>.Failure("The typed native intent must match this operation and selected wallet.", operation);
        var operationId = DeploymentIdentity.Operation(actor.Value, operation.ActingTenantId, key);
        operation.Id = Guid.ParseExact(operationId, "N");
        operation.InitiatorUserId = actor;
        operation.IdempotencyKey = CanonicalJson.Hash(new { domain = "deployment-operation-projection/1", operationId });
        operation.Parameters["DeploymentRequestKey"] = key;
        operation.Parameters["DeploymentOperationId"] = operationId;
        operation.Parameters["ChainType"] = start.Target.ChainType;
        operation.Parameters["ChainNetwork"] = start.Target.Network;
        var admitted = await _deployments.StartWithCredentialAsync(actor.Value, operation.ActingTenantId, key, start, request?.AuthenticationWitness);
        if (admitted.IsError || admitted.Result is null)
            return AZOAResult<IBlockchainOperation>.Failure(admitted.Message, operation);
        ProjectNative(operation, admitted.Result);
        return await _blockchainOperationStore.UpsertAsync(operation, default);
    }

    private async Task RefreshNativeAsync(IBlockchainOperation operation)
    {
        if (operation.OperationType is not ("DeployContract" or "CallContract")
            || !operation.Parameters.TryGetValue("DeploymentOperationId", out var id)) return;
        if (_deployments is null) throw new InvalidOperationException("Deployment service registration is required.");
        var actor = operation.InitiatorUserId ?? operation.UserId;
        if (actor is null || actor == Guid.Empty || operation.UserId != actor)
            throw new InvalidOperationException("Native operation projection has inconsistent caller ownership.");
        var current = await _deployments.GetAsync(id, actor.Value, operation.ActingTenantId);
        if (current.IsError || current.Result is null)
        {
            operation.Status = "NeedsOperator";
            operation.CompletedDate = null;
            operation.Parameters["Error"] = current.Message;
            return;
        }
        ProjectNative(operation, current.Result);
    }

    private static void ProjectNative(IBlockchainOperation operation, DeploymentReceipt receipt)
    {
        operation.Parameters["DeploymentOperationId"] = receipt.OperationId;
        operation.Parameters["DeploymentReceipt"] = JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        operation.Parameters.Remove("Error");
        operation.Parameters.Remove("TxHash");
        if (receipt.TransactionIds.LastOrDefault() is { } tx) operation.Parameters["TxHash"] = tx;
        operation.Status = receipt.State == "Confirmed"
            ? operation.OperationType == "DeployContract" ? OperationStatus.Deployed : OperationStatus.Called
            : receipt.State;
        if (receipt.State is "Confirmed" or "Failed" or "Cancelled") operation.CompletedDate ??= DateTime.UtcNow;
        else operation.CompletedDate = null;
    }
}
