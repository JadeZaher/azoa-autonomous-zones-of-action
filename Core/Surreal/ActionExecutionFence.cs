using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Core.Surreal;

/// <summary>Serializes a local effect against cancellation and the admitted action intent.</summary>
public static class ActionExecutionFence
{
    public const string FailureMarker = "ACTION_EXECUTION_AUTHORITY_CHANGED";

    // raw: cross-record run/execution authority participates in its caller's atomic local-effect transaction.
    public static IReadOnlyList<SurrealQuery> Create(string runId, string nodeId, string actorId, string? tenantId, string intentHash) =>
    [
        SurrealQuery.Of("""
            LET $_effect_run = (UPDATE ONLY type::record('action_run', $_effect_run_id) SET status = status
                WHERE status INSIDE ['Running', 'AwaitingReconciliation'] AND user_id = type::record('user', $_effect_actor)
                    AND ((!$_effect_has_tenant AND acting_tenant_id = NONE)
                        OR ($_effect_has_tenant AND acting_tenant_id = type::record('user', $_effect_tenant))) RETURN AFTER)
            """).WithParam("_effect_run_id", runId).WithParam("_effect_actor", actorId)
            .WithParam("_effect_has_tenant", tenantId is not null).WithParam("_effect_tenant", tenantId ?? string.Empty),
        SurrealQuery.Of("IF $_effect_run = NONE { THROW 'ACTION_EXECUTION_AUTHORITY_CHANGED' }"),
        SurrealQuery.Of("""
            LET $_effect_execution = (UPDATE action_node_execution SET state = state
                WHERE run_id = type::record('action_run', $_effect_run_id) AND node_id = type::record('action_node', $_effect_node_id)
                    AND state = 'Running' AND crypto::sha256(frozen_input) = $_effect_hash RETURN AFTER)
            """).WithParam("_effect_run_id", runId).WithParam("_effect_node_id", nodeId).WithParam("_effect_hash", intentHash),
        SurrealQuery.Of("IF array::len($_effect_execution) != 1 { THROW 'ACTION_EXECUTION_AUTHORITY_CHANGED' }")
    ];
}
