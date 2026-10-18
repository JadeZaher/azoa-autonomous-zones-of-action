-- node-admin-rbac-cli_20261018 lane A2 (OperatorActor): operator-written domain audits record the real actor
-- (`legacy-operator` | `admin:<id>` | `root:<fingerprint>`) beside the operator-subject user link. These tables
-- already have rows on a deployed database, so the new optional field reaches them only through this forward
-- DEFINE FIELD OVERWRITE; see ../AGENTS.md §old-row-rule, ../CompatibilityBaselines/README.md and
-- ../../../Services/NodeAdmin/AGENTS.md §operator-actor. option<string> keeps old rows writable and reads NONE on
-- rows written before named node admins; no CAS reads the field, so no backfill. READONLY like the other audit columns
-- (a written actor can never be rewritten; amended before first deploy, security review L-5).
DEFINE FIELD OVERWRITE actor ON TABLE kyc_control_audit TYPE option<string> READONLY;
DEFINE FIELD OVERWRITE actor ON TABLE node_governance_audit TYPE option<string> READONLY;
DEFINE FIELD OVERWRITE actor ON TABLE node_fee_audit TYPE option<string> READONLY;
DEFINE FIELD OVERWRITE actor ON TABLE node_treasury_audit TYPE option<string> READONLY;
DEFINE FIELD OVERWRITE actor ON TABLE action_recovery_audit TYPE option<string> READONLY;
