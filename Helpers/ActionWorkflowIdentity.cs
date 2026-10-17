namespace AZOA.WebAPI.Helpers;

/// <summary>Stable application identities for admitted workflow steps.</summary>
public static class ActionWorkflowIdentity
{
    /// <summary>Scopes a workflow start key to its authenticated actor, tenant and action.</summary>
    public static Guid RunId(Guid actionId, Guid actor, Guid? tenant, string key) =>
        Guid.ParseExact(CanonicalJson.Hash(new { Domain = "action-run-request-v1", actionId, actor, tenant, key })[..32], "N");

    /// <summary>Returns the unique persisted saga step identity for one run and node.</summary>
    public static string StepId(Guid runId, Guid nodeId) =>
        CanonicalJson.Hash(new { Domain = "action-step-v1", RunId = runId, NodeId = nodeId })[..32];
}
