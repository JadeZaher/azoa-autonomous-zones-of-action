using System.Text.Json;
using System.Text.Json.Nodes;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Builds safe action-node operation output while retaining supported binding fields.</summary>
internal static class ActionNodeOutputProjection
{
    /// <summary>Serializes an operation result without its internal parameter or initiator metadata.</summary>
    public static string SerializeOperation(AZOAResult<IBlockchainOperation> result) =>
        JsonSerializer.Serialize(Project(result, BlockchainOperationResponse.From), ActionNodeJson.Options);

    /// <summary>Serializes a fungible launch result through its strict public allowlist.</summary>
    public static string SerializeFungible(AZOAResult<FungibleTokenResult> result) =>
        JsonSerializer.Serialize(Project(result, FungibleTokenResultResponse.From), ActionNodeJson.Options);

    /// <summary>Projects a durable execution for an HTTP response without altering its internal binding output.</summary>
    public static ActionNodeExecutionResponse ToPublic(ActionNodeExecution execution) =>
        ActionNodeExecutionResponse.From(execution, SanitizeForPublic(execution.Output));

    /// <summary>Projects a durable execution aggregate for an HTTP response.</summary>
    public static ActionExecutionStateResponse ToPublic(ActionExecutionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new ActionExecutionStateResponse
        {
            RunId = state.RunId,
            ActionId = state.ActionId,
            Status = state.Status,
            StartedAt = state.StartedAt,
            EndedAt = state.EndedAt,
            TotalNodes = state.TotalNodes,
            CompletedNodes = state.CompletedNodes,
            FailedNodes = state.FailedNodes,
            PendingNodes = state.PendingNodes,
            NodeExecutions = state.NodeExecutions.Select(ToPublic).ToArray(),
        };
    }

    /// <summary>Redacts legacy persisted operation output before it crosses an HTTP boundary.</summary>
    private static string? SanitizeForPublic(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return output;

        try
        {
            var node = JsonNode.Parse(output);
            return node is null ? null : Sanitize(node)?.ToJsonString(ActionNodeJson.Options);
        }
        catch (JsonException)
        {
            // A malformed legacy output is not useful to an API caller and must not
            // bypass the output allowlist just because it cannot be projected.
            return null;
        }
    }

    private static JsonNode? Sanitize(JsonNode node)
    {
        if (node is JsonObject source)
        {
            if (LooksLikeBlockchainOperation(source))
                return ProjectOperation(source);
            if (LooksLikeFungibleTokenResult(source))
                return ProjectFungible(source);

            var projected = new JsonObject();
            foreach (var property in source)
            {
                // Details are exception snapshots and idempotency keys are
                // server-only correlation data, never durable output bindings.
                if (string.Equals(property.Key, "Detail", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Key, "IdempotencyKey", StringComparison.OrdinalIgnoreCase))
                    continue;

                projected[property.Key] = property.Value is null ? null : Sanitize(property.Value);
            }
            return projected;
        }

        if (node is JsonArray array)
        {
            var projected = new JsonArray();
            foreach (var item in array)
                projected.Add(item is null ? null : Sanitize(item));
            return projected;
        }

        return node.DeepClone();
    }

    private static JsonNode? ProjectOperation(JsonObject source)
    {
        try
        {
            var operation = source.Deserialize<BlockchainOperation>(ActionNodeJson.Options);
            return operation is null
                ? null
                : JsonSerializer.SerializeToNode(
                    BlockchainOperationResponse.From(operation),
                    ActionNodeJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? ProjectFungible(JsonObject source)
    {
        try
        {
            var result = source.Deserialize<FungibleTokenResult>(ActionNodeJson.Options);
            return result is null
                ? null
                : JsonSerializer.SerializeToNode(
                    FungibleTokenResultResponse.From(result),
                    ActionNodeJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool LooksLikeBlockchainOperation(JsonObject value) =>
        Has(value, "OperationType")
        && Has(value, "Status")
        && (Has(value, "Parameters")
            || Has(value, "IdempotencyKey")
            || Has(value, "InitiatorUserId")
            || Has(value, "InitiatorApiKeyId")
            || Has(value, "ActingTenantId")
            || Has(value, "SigningScope"));

    private static bool LooksLikeFungibleTokenResult(JsonObject value) =>
        Has(value, "UserId")
        && Has(value, "WalletId")
        && Has(value, "WalletAddress")
        && Has(value, "WalletProvisioned")
        && Has(value, "AssetId");

    private static bool Has(JsonObject value, string name) =>
        value.ContainsKey(name)
        || value.Any(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase));

    private static AZOAResult<TOutput> Project<TInput, TOutput>(
        AZOAResult<TInput> result,
        Func<TInput, TOutput> project)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(project);

        return new AZOAResult<TOutput>
        {
            IsError = result.IsError,
            Message = result.Message,
            Result = result.Result is null ? default : project(result.Result),
            Code = result.Code,
            RetryAfterSeconds = result.RetryAfterSeconds,
        };
    }
}
