using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Helpers;

/// <summary>Checks claim snapshot authority and database time inside an atomic typed mutation.</summary>
public static class PersonOperationClaimFence
{
    public const string Changed = "PERSON_CLAIM_STATE_CHANGED";
    public const string Closed = "PERSON_CLAIM_WINDOW_CLOSED";
    public const string Invalid = "PERSON_CLAIM_INVALID_LEGACY_STATE";

    // raw: database cutoff and expected claim state guard the typed writer in the same transaction.
    public static IReadOnlyList<SurrealQuery> Create(PersonOperationClaim identity, PersonOperationClaim? expected, bool decision)
        => [
            SurrealQuery.Of("LET $_claim = (SELECT * FROM ONLY type::record('person_operation_claim', $_claim_id))")
                .WithParam("_claim_id", identity.Id),
            SurrealQuery.Of("""
                IF $_claim != NONE AND ($_claim.version = NULL OR $_claim.user_id = NULL OR $_claim.tenant_id = NULL
                    OR $_claim.claimed_at = NULL OR $_claim.released_at = NULL OR $_claim.decision_option = NULL
                    OR $_claim.decided_at = NULL OR $_claim.decision_closes_at = NULL
                    OR ($_claim.version != NONE AND ($_claim.version < 0 OR $_claim.version >= 9223372036854775807))) { THROW 'PERSON_CLAIM_INVALID_LEGACY_STATE' }
                """),
            SurrealQuery.Of("""
                IF ($_claim_exists AND $_claim = NONE) OR (!$_claim_exists AND $_claim != NONE)
                    { THROW 'PERSON_CLAIM_STATE_CHANGED' }
                """).WithParam("_claim_exists", expected is not null),
            SurrealQuery.Of("""
                IF $_claim != NONE AND ($_claim.uniqueness_scope != $_claim_scope OR $_claim.operation_key != $_claim_operation
                    OR $_claim.person_key != $_claim_person) { THROW 'PERSON_CLAIM_INVALID_LEGACY_STATE' }
                """).WithParam("_claim_scope", identity.UniquenessScope).WithParam("_claim_operation", identity.OperationKey)
                    .WithParam("_claim_person", identity.PersonKey),
            SurrealQuery.Of("""
                IF $_claim != NONE AND ((IF $_has_version THEN $_claim.version != $_version ELSE $_claim.version != NONE END)
                    OR (IF $_has_holder THEN $_claim.user_id != $_holder ELSE $_claim.user_id != NONE END)
                    OR (IF $_has_release THEN $_claim.released_at != $_release ELSE $_claim.released_at != NONE END)
                    OR (IF $_has_close THEN $_claim.decision_closes_at != $_close ELSE $_claim.decision_closes_at != NONE END))
                    { THROW 'PERSON_CLAIM_STATE_CHANGED' }
                """).WithParam("_has_version", expected?.Version is not null).WithParam("_version", expected?.Version ?? 0)
                    .WithParam("_has_holder", expected?.UserId is not null).WithParam("_holder", expected?.UserId ?? string.Empty)
                    .WithParam("_has_release", expected?.ReleasedAt is not null).WithParam("_release", expected?.ReleasedAt ?? DateTimeOffset.UnixEpoch)
                    .WithParam("_has_close", expected?.DecisionClosesAt is not null).WithParam("_close", expected?.DecisionClosesAt ?? PersonOperationClaim.NeverClosesAt),
            SurrealQuery.Of("""
                IF ($_claim != NONE AND $_claim.decision_closes_at != NONE AND $_claim.decision_closes_at <= time::now())
                    OR ($_new_decision AND ($_claim = NONE OR $_claim.decision_closes_at = NONE) AND $_requested_close <= time::now())
                    { THROW 'PERSON_CLAIM_WINDOW_CLOSED' }
                """).WithParam("_new_decision", decision)
                    .WithParam("_requested_close", identity.DecisionClosesAt ?? PersonOperationClaim.NeverClosesAt)
        ];

    public static TypedUpdateOnlyBuilder<PersonOperationClaim> Update(PersonOperationClaim expected)
    {
        var update = SurrealWriter.UpdateOnly<PersonOperationClaim>(expected.Id)
            .Where(row => row.UniquenessScope == expected.UniquenessScope && row.OperationKey == expected.OperationKey && row.PersonKey == expected.PersonKey);
        update = expected.Version is null ? update.Where(row => row.Version == null) : update.Where(row => row.Version == expected.Version);
        update = expected.UserId is null ? update.Where(row => row.UserId == null) : update.Where(row => row.UserId == expected.UserId);
        update = expected.ReleasedAt is null ? update.Where(row => row.ReleasedAt == null) : update.Where(row => row.ReleasedAt == expected.ReleasedAt);
        update = expected.DecisionClosesAt is null ? update.Where(row => row.DecisionClosesAt == null) : update.Where(row => row.DecisionClosesAt == expected.DecisionClosesAt);
        return update.Set(row => row.Version, expected.Version == long.MaxValue ? long.MaxValue : (expected.Version ?? 0) + 1);
    }
}
