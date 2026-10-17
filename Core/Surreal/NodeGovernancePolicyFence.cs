using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Core.Surreal;

/// <summary>Serializes policy-sensitive effects with singleton policy updates, including initial policy creation.</summary>
public static class NodeGovernancePolicyFence
{
    public const string FailureMarker = "NODE_GOVERNANCE_POLICY_CHANGED";

    public static async Task<long?> ReadVersionAsync(ISurrealExecutor executor, CancellationToken ct = default)
        => (await executor.QuerySingleAsync<NodeGovernanceParameters>(
            SurrealQuery<NodeGovernanceParameters>.Key(NodeGovernanceParameters.LocalId), ct))?.Version;

    public static SurrealQuery Lock() => SurrealGeneratedStatementScope.Apply(SurrealWriter.Upsert(
        new NodeGovernancePolicyLock { Token = Guid.NewGuid().ToString("N") }), "policy_lock");

    // raw: singleton version/absence is checked under the same lock used by policy writers and effect commit.
    public static IReadOnlyList<SurrealQuery> Create(long? expectedVersion) =>
    [
        Lock(),
        SurrealQuery.Of("""
                LET $_effect_policy = (SELECT * FROM ONLY node_governance_parameters:local)
                """),
            SurrealQuery.Of("""
                IF ($_effect_policy_exists AND ($_effect_policy = NONE OR $_effect_policy.version != $_effect_policy_version))
                OR (!$_effect_policy_exists AND $_effect_policy != NONE) { THROW 'NODE_GOVERNANCE_POLICY_CHANGED' }
                """).WithParam("_effect_policy_exists", expectedVersion.HasValue)
                .WithParam("_effect_policy_version", expectedVersion ?? 0L)
    ];
}
