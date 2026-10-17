# Durable DEX stages

`SwapStageCoordinator` is the internal stage boundary. Transport/domain callers
first authorize private operation access and select the provider for the saved
chain/network. The coordinator independently binds that module and all saved
intent/plan/candidate hashes. No public request supplies accounting evidence.

Custody admission advances the planned stage and operation epochs under the
shared current signing/action fence. It does not permit egress. Cancellation
may still win while signing runs; saving and first-send authorization must
then fail their exact epochs. A retry that only finds a prior plan is never
a substitute for this live check. Browser disclosure is not yet public: its
required exposure-before-disclosure transition is tracked in the Round 3 DEX
contract. Executable unsigned wire must not be returned through ordinary views.

Recovery observes before replay, and only sends already-authorized canonical
bytes while their operation/stage remains pending. It neither plans nor signs
new transactions. Confirmed effects with unknown incremental rent remain
separate from accounting completion; only the original provider's historical
proof can resolve that reservation. Infrastructure errors propagate to the
worker/HTTP boundary with the original durable wire intact.

Cleanup is a new direct participant command with current wallet/grant authority;
it does not reactivate a completed workflow. Rent release advances a persisted
provider checkpoint one bounded history page at a time. Only original known
allocation can be released, once. Original allocation totals remain historical;
the separate release total reduces outstanding locked balance. Unknown allocation
keeps its entire reservation until its earlier accounting proof is resolved.

The authenticated `/api/dex` boundary captures the real credential, persists
immutable quotes and command identities, and projects receipts without private
wire. Cleanup has a separate participant command and fee ceiling. Completion
requires finalized cleanup plus attributed release; it preserves any earlier
economic settlement. Missing configured modules return unavailable.

Recovery advances each due operation's retry time before contacting providers.
Accounting traverses persisted 25-stage pages, advancing the cursor before
provider work so an unresolved ancestor does not monopolize later history.
Only saved, submission-authorized wire may replay. Neither a worker nor polling
can create signatures or replace an accepted quote. The old swap routes remain
pending the full provider/client cutover; source wiring is not runtime evidence.

External signing history stores a nullable real candidate plus an explicit original exposed plan for finalized absence. The absence proof is provider validated before journal admission and revalidated before a successor; it never manufactures a signature. `ServerSubmissionWithdrawnAt` disables future server authorization/replay but preserves original potentially-sent history and does not recall already admitted/in-flight requests. Observation and accounting continue. No cancellation or budget release follows from withdrawal alone.
