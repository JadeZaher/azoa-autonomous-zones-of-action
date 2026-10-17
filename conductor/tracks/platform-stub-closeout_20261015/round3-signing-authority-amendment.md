---
type: Design Amendment
title: Shared signing admission authority for direct and workflow effects
status: accepted-for-staged-implementation
---

# Contract

Lane B owns the shared DTO, principal snapshot factory and transaction fence. Lane A retains the DEX adapter and supplies its required scope and bound wallet/quote identity. This extends the accepted DEX fence without manufacturing authentication evidence for workers. Root independently accepted this contract; source implementation and separate review are in progress.

## Discriminated authority

`SigningAdmissionAuthority` is server-only and versioned. Its kind is `DirectJwt`, `DirectApiKey`, or `Workflow`. Every variant binds authenticated actor, acting tenant, required signing scope(s), exact owned wallet ID, stored address and chain, plus immutable credential and selected-grant witnesses. Client JSON cannot populate this object.

For JWT requests, the factory requires the actual authenticated identity and a validated credential timestamp: login `auth_time`, or child `nbf`, plus `exp`. Existing login tokens do not emit `iat`; using the authenticated `auth_time`/`nbf` already present is explicit rather than pretending it exists. The transaction verifies active user, original issued/not-before time against `auth_not_before`, expiry and current exact wallet ownership/address/chain. A new authenticated Continue may carry a new direct snapshot, but it cannot change the original asset intent, quote or prepared bytes.

API keys are a real supported authentication method and emit no JWT lifetime. Their distinct variant binds the exact authenticated API key ID, owner, scopes and expiry witnessed from its database row, with active/non-revoked/current-scope checks in the transaction. It also freezes the user's observed auth watermark and requires it unchanged at new-effect authorization. The factory never fabricates `iat` or `exp`. Empty API-key scopes retain the existing documented full-access policy; forbidden/restricted scope handling must match the authentication handler. A key cannot assert another acting tenant.

For workflow admission, persist `RunAuthenticationWitness` on the run in the same transaction that creates the frozen run/outbox. It captures the authenticated credential class and original user revocation watermark, actor/tenant and authorized credential scope bounds. It is not an ongoing JWT expiry and does not synthesize one. API-key-origin runs additionally retain the actual key identity and require that key remain active/non-revoked for new effects. The run witness is immutable across replay; forked new work requires fresh explicit authorization, rather than silently inheriting authority after revocation.

At each economic node's Pending-to-Running admission, choose exact live required-scope grants, lock them and save `NodeSigningAuthority` atomically with the run epoch, node frozen-input hash and external reference. Different required scopes may be covered by different valid grants, preserving the earlier accepted split-grant behavior; each scope maps to one deterministic selected grant ID with exact scopes/origin/owner/tenant/expiry witness. `UserExplicit` is required for delegated signing; participation/read/payment grants cannot substitute. A grant selected at admission cannot be silently replaced later.

A workflow new-effect transaction joins ActionExecutionFence, the saved node authority and current user revocation/wallet/grant checks. A worker may use that persisted authority only for actions the existing domain contract allows; it does not authorize fresh participant signatures. A participant's explicit continuation can supply current direct authority tied to the original operation, while the action/run cancellation fence still applies before a new economic authorization.

## Mutation and recovery boundaries

The shared fence accepts a validated discriminated witness, required scopes, exact domain wallet binding and unique parameter prefix. It emits flat statements suitable for the owning atomic transaction. It neither starts nor commits a transaction. All user/key/grant/wallet changes that invalidate these witnesses must serialize through the records it locks. The owning domain remains responsible for quote consumption, request cancellation competition, generation CAS and signed-wire first-send authorization.

No-effect owner cancellation is a separate explicit policy: authenticated original owner can stop an unsent request after delegated grant loss. It cannot enable a new spend. Already-authorized exact wire observation/rebroadcast and historical receipt projection do not invent a fresh credential or renew an expired direct authorization. The domain must distinguish these from initial send and replacement generation.

## Migration and compatibility

Existing frozen runs/nodes lacking the run/node witness are not backfilled from current users or grants. New economic effects park with `NeedsParticipantAuthorization`. An authenticated original participant may authorize the exact frozen run/node/intent using an audited, version-CAS recovery endpoint; this freezes a new current witness and wakes only that node. Original signed wire and final receipts remain observable without synthesizing historical authority. Unknown witness versions and credential kinds refuse new effects.

Direct non-HTTP internal callers (genesis and tests) cannot impersonate an authenticated principal. Genesis must use its actual operator authorization captured by the operator endpoint; deterministic worker continuation observes only previously authorized wire. Test fixtures construct explicit test witnesses but production services never use that path.

## Source evidence and acceptance

`ApiKeyAuthenticationHandler` emits ApiKeyId/AuthMethod and scope claims, not JWT timestamps. `UserLoginTokenIssuer` emits auth_time/exp. `TenantManager.GenerateChildJwt` emits nbf/exp and delegated scopes. The shared factory must inspect these actual contracts and reject malformed/missing evidence rather than substituting wall-clock time.

Required authored regressions: revoked/expired JWT and API key, user auth-watermark advance, wallet mutation, exact selected-grant revocation with a different substitute grant still available, split grants, wrong actor/tenant, stale run epoch/cancel, delayed preparation/first-send after revocation, direct versus workflow expiry distinction, missing legacy witness recovery, fresh participant reauthorization bound to exact intent, and already-authorized exact-wire observation after later revocation. Real Surreal interleaving fixtures must prove the transaction fence, not only pre-read validation. No runtime gates run before the final integrated sweep.

## Independent review clarifications

Root accepted with two binding additions: original run credential scope bounds constrain every node, so new live grants cannot widen the originally authorized credential; a fresh direct Continue may authorize only the exact preserved operation and must not overwrite its immutable historical authority or wire witnesses. The JWT watermark reader and current issuers use the validated credential class timestamps described above, with no wall-clock fallback.
