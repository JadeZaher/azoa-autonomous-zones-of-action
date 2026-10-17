---
type: plan
status: accepted-staged-implementation
---

# Platform genesis on durable fungible creation

Root independently accepted this amendment before implementation. Source and runtime acceptance remain separate; runtime gates have not run.

The existing `PlatformTokenGenesis` claims a frozen platform-unit definition, then calls the ordinary fungible manager without an authenticated credential and assumes a synchronous asset ID. That is incompatible with the repaired lifecycle. Genesis must reuse the provider preparation/submission/finality rail without granting node operators ordinary user signing authority.

## Narrow authority

Introduce a separate server-only platform-genesis authority witness on the fungible command, mutually exclusive with ordinary direct/workflow signing authority. It contains the actual validated node-operator credential, the exact pending `platform_token` record identity and immutable definition hash, original genesis operation key, original logical operator identity, explicit network/genesis and reserve address. It accepts only `TokenUseNodeOperator`, reserved node-operator user ID, active original user/auth watermark, current bootstrap credential/session revisions, and a recent operator session. No generated principal, fabricated authentication time, unrestricted owner flag, arbitrary token scope, tenant, user wallet, or arbitrary platform operation is permitted.

Admission and every new prepare/first-send authorization transaction lock that exact pending genesis definition and current operator credential/bootstrap revisions. The canonical frozen definition binds chain/network/name/unit/decimals/total and the platform reserve creator/manager/reserve/freeze/clawback roles. The provider still validates its configured reserve signer and canonical genesis. Ordinary creation cannot supply this witness through JSON. The manager exposes a narrow internal genesis admission path; regular KYC/readiness gates remain unchanged for ordinary callers. Genesis is node bootstrap authority, not a pretend verified person.

Fresh operator continuation may record a new authorization receipt for only the same admitted genesis operation. It cannot replace the original definition, reserve address, historical authority or prepared bytes. A current operator session after revocation does not silently make an old signature newly authorized. Existing platform-token rows without a stored operation link require explicit current operator recovery that pins the existing exact frozen definition; active rows never create another asset.

## Public journey and projection

`POST /api/admin/platform-token/genesis` keeps the irreversible-definition acknowledgement, captures the actual validated operator credential, claims/replays the deterministic pending definition, and admits a creation operation without signing. A pending response returns its actual lifecycle receipt and versioned cost quote. Explicit quote acceptance and continuation use dedicated recent-operator routes with original operation identity and request key. The generic participant endpoints cannot accept a platform-purpose witness by assertion. Subsequent reads, refresh and restricted workers may observe/rebroadcast already-authorized bytes and account for final evidence without a new operator session.

Activation of `platform_token` must join the same finalized authority/reserve/projection transaction, checking the original pending definition and operation binding. No separate write can report an active platform unit before the reserve ledger exists. After confirmation a crash or expired operator session cannot strand accounting. Pending/unknown evidence cannot become an asset ID or active row. A conflicting platform definition remains a concrete typed conflict, never a second issuance.

Devnet faucet funding is a separate explicitly disclosed prerequisite, not a production mint success signal. It must not run blindly on every polling/replay call. Existing actual reserve balance determines whether the configured dev/test faucet is needed; Mainnet never invokes a faucet. Lack of funds returns the pending operation with the exact funding requirement.

## Acceptance and ownership

Lane B owns the authority discriminator, genesis service/store/controller plumbing, final activation join, SDK/operator UI and fixtures. Root independently reviews before implementation. Required authored fixtures include ordinary-user and forged-purpose denial, wrong definition/network/reserve, stale operator revisions, pending response with no asset, consent before first send, crash after chain finality before activation, replay after session revocation, and exactly one active platform unit/reserve/authority under overlap. All runtime gates remain reserved for the final integrated sweep.
