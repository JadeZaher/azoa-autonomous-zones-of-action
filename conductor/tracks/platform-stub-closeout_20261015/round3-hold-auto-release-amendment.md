---
type: design-amendment
---

# Exact automatic-release mandate

SOURCE_DESIGN_VERDICT: ACCEPTED by Root with the binding cost-category and source-fence corrections below. No runtime gates run.

A boolean AutoRelease is not economic consent. The usable journey admits the hold, then the original owner explicitly enables one exact release mandate with the current hold/version, fixed beneficiary wallet/address, chain/network/protocol/asset/quantity, release-not-before and final mandate-expiry, and a native transfer fee ceiling. The displayed quote binds mechanics and currency units. New mandates require current actual credential, transfer scope, same custody tenant, owner and original liability. Existing boolean requests must require these complete fields; never infer a destination or unbounded fee from the beneficiary profile.

A new HoldReleaseMandate record is immutable apart from cancellation/status/version and binds its original canonical SigningConsentWitness, the exact source hold/version/definition hash, fee ceiling, recipient and policy. Activation joins current authority, aggregate lock and mandate create atomically. A stable actor/tenant/request identity returns the original receipt. Explicit revocation wins the same record lock as first-egress authorization; submitted bytes remain observable.

Worker authority is a persisted mandate reference, mutually exclusive with direct/workflow/economic-source authority. It is neither a synthetic JWT nor an inferred participant grant. A narrow shared ForAcceptedMandate consent fence may reuse the original credential ceiling and exact user/auth watermark/key/grants while omitting JWT wall-clock expiry only, exactly as frozen workflow execution does. The owning transaction must also lock and compare the complete original accepted mandate record/hash, live status/expiry, exact liability and original action fence if present. Actual key expiry/revocation, delegated grant expiry/revocation, account watermark changes and inactive users still stop new effects. A current actor can reauthorize only the same immutable mandate terms with an appended receipt; historical authority remains preserved.

The mandate admits only deterministic Release command identity derived from its record and liability. It cannot fund, refund, change beneficiary, shorten the cutoff, select another asset or draw another partition. Worker obtains current provider mechanics/readiness and a fresh quote within the already accepted native fee ceiling; an increased fee or changed mechanics parks for explicit participant action, never raises the ceiling. The new quote and immutable mandate source are recorded before provider preparation. Native fees and minimum-balance costs remain distinct. No fee is charged twice merely for recovery.

Platform custody may prepare and send under this original mandate. Recipient acceptance is a separate actual-recipient action; the worker cannot obtain their signature or change their wallet. If recipient is unready, the command is discoverable as AwaitingParticipant. After confirmed acceptance, the mandate can continue its exact platform release while current mandate authority remains valid. A signed or possibly sent release excludes cancellation/refund/chaining until exact no-effect evidence. Generation replacement is never automatic: it uses the existing explicit proof/quote/recovery receipt journey.

Acceptance fixtures: mandate cannot change asset/destination/amount/window/ceiling; expired JWT does not invent a new session yet an accepted mandate can execute, while revoked user/key/grant/mandate cannot; cancellation-vs-firstsend is serialized; two workers admit one release; recipient unready never invokes participant custody; fee increase parks; release cutoff and mandate expiry use DB time; completed accounting survives later authority expiry; browser-close observation converges exact bytes; legacy boolean has no invented authority.


## Binding review corrections

The immutable mandate separately caps network fees, locked/minimum balances and
nonrefundable protocol costs, each with exact payer and refund recipient. Empty
or unapproved categories have zero authorization, never unlimited authorization.
A provider quote must explicitly account for every category; missing breakdown
is unavailable for mandate admission. Recipient costs remain separately authorized
by the actual recipient; the platform mandate cannot pay or sign on their behalf.

Expiry handling is not a generic ForAcceptedMandate bypass: only the owning store
may reconstruct the original consent from its exact persisted mandate and compose
its hash/version/live-status/database-time fences with current actor/key/grant
checks. No request-supplied witness can invoke this authority. Reauthorization
appends a receipt for unchanged terms and cannot resurrect a cancelled or terminal
mandate; a different intent requires a new explicit request/mandate.
