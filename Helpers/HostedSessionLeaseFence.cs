using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Helpers;

/// <summary>Atomic transaction guard for writes owned by a hosted-session lease.</summary>
public static class HostedSessionLeaseFence
{
    public const string FailureMarker = "HOSTED_LEASE_LOST";

    /// <summary>Write-locks the current unexpired lease inside the caller's domain transaction.</summary>
    public static SurrealQuery CreateGuard(HostedSession lease)
        // raw: transaction-level lease fencing across domain records; see AGENTS.md.
        => SurrealQuery.Of("""
            LET $_lease = (UPDATE type::record($_hosted_table, $_hosted_id)
                SET modified_date = time::now()
                WHERE state = 'RESUMING' AND resume_lease_token = $_lease_token
                    AND resume_lease_expires_at > time::now()
                    AND (provider_evidence_conflict = NONE OR provider_evidence_conflict = false)
                RETURN AFTER)
            """)
            .WithParam("_hosted_table", HostedSession.SchemaNameConst)
            .WithParam("_hosted_id", lease.Id)
            .WithParam("_lease_token", lease.ResumeLeaseToken);

    /// <summary>Aborts the surrounding transaction when the ownership guard matched no row.</summary>
    public static SurrealQuery AssertCurrent()
        // raw: assertion belongs to the same atomic lease/domain transaction.
        => SurrealQuery.Of("IF array::len($_lease) != 1 { THROW 'HOSTED_LEASE_LOST' }");
}
