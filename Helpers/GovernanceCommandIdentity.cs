using AZOA.WebAPI.Models.Actions;
using System.Text.Json;

namespace AZOA.WebAPI.Helpers;

/// <summary>Canonical governance command identities and immutable payload digests.</summary>
public static class GovernanceCommandIdentity
{
    public static string Id(ActionNodeExecutionContext context) => CanonicalJson.Hash(new
        { domain = "azoa-governance-v1", context.RunId, context.NodeId, context.ActingUserId, context.ActingTenantId })[..32];

    public static string IntentHash(ActionNodeExecutionContext context)
    {
        using var config = JsonDocument.Parse(context.Node.Config);
        return CanonicalJson.Hash(new ActionFrozenNodeInput(1, context.RunId, context.NodeId, context.Node.NodeType,
            context.ActingUserId, context.ActingTenantId, config.RootElement, context.UpstreamExecutions, context.AllRunExecutions));
    }

    public static ActionExternalOperationReference Reference(ActionNodeExecutionContext context, string intentHash)
        => new(1, "governance", $"action:{context.RunId:N}:{context.NodeId:N}:governance", Id(context),
            intentHash, context.ActingUserId, context.ActingTenantId);
}
