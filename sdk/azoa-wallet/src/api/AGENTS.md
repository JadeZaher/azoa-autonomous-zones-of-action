# API contracts

The workflow-designer interview route omits its entire brief/history from SDK debug logging. Its default client deadline is 95 seconds so the API's bounded 90-second model call can return a typed timeout; an explicitly configured SDK timeout remains authoritative. No designer request saves, publishes, starts a run, or moves funds.

`workflow-designer.ts` mirrors the public contract, including explicit `planningScopeAccepted`, canonical state, separate professional-review questions and limitations. `hasWorkflowPlanningDraft` requires `DraftReady`, the readiness flag and a nonempty private Emit-only graph; consumers separately require scope consent and review. This graph records fixed planning/manual-external-handoff notes, implementing no approval, eligibility, cap, refund, payout or settlement controls. It is not an automated economic workflow. The model selects closed IDs privately; public wording/configs are server-owned. User amounts and contextual unknown-payment recovery travel unchanged in the private request. Legal/tax checklists are not professional determinations. Manual authoring remains available.

Import does not execute the graph. A later explicit run of Emit nodes requires an active tenant registration and can enqueue webhook notifications to its configured destination. Checkpoints do not wait for or prove review/approval. Consumers must preserve the server's limitations and require acknowledgment of this side effect before import; graph execution remains a separate explicit operation.

NFT lifecycle methods return durable operation views, including HTTP 202 pending
states. Completed alone establishes confirmed chain completion. Request keys are
required for mint/transfer/burn/issue/refund; reuse the same key and body after an
interrupted request. Legacy NFT mutation projections remain supported with keys.

Fee quotes expose decimal base-unit strings, revision and payer before authorization.
Never round these strings through JavaScript numbers. Mint/transfer/revoke/refund
carry feeAuthorization; DELETE burn carries the equivalent query fields.

Credential revoke returns pending success envelopes carrying lifecycleId/lifecycleState;
isConfirmed remains false until chain completion. Older pending-error envelopes remain
recognizable. Legacy NFT mint/transfer/burn carry body feeAuthorization.
Request-key lookup and cancellation are authenticated actor/tenant scoped. Lookup absence
is uncertain; only successful server cancellation/terminal correlation clears a saved key.
Cursor discovery can return an empty items array with a nextCursor; continue paging.
Ownership and access verification return booleans, not success-shaped objects.

Wallet authentication signs the server challenge message verbatim: Ethereum uses
EIP-191 personal-message signing; Algorand/Solana use the raw UTF-8 message with
Ed25519. Use the canonical address and chain returned in the challenge; never
lowercase a Solana address. Proof/claim calls are anonymous and do not inherit a
tenant API key or stale JWT. Linking requires recent first-party authentication.
Successful ownership claim revokes the previous password and wallet credential.
Hosted recovery APIs expose minimized attempt state, never provider evidence or
credentials. Refresh reuses the original durable request; reconciliation requires
reviewed evidence and cannot manually approve an external verification.

Deployment v2 explicitly separates runtime activation from native chain operations.
Generation returns the normalized compiled artifact; quote that exact artifact and
retain the returned intent and request key. HTTP 202 is pending. Discovery/continuation
uses the durable server operation; request-key cancellation fences delayed admission.
Only Confirmed establishes native completion or runtime activation. Invocation returns
an admitted action run to observe, not immediate workflow success. Quote estimates,
reserved limits and confirmed fees/balance changes are separate amount-string fields.

Fungible creation requires a persisted explicit admission key. Total supply is canonical uint64 text (safe integer numbers normalize to text); no unsafe JavaScript-number truncation. HTTP 202 returns a durable operation, never minted success. Quote acceptance, exact-version continuation, no-effect replacement, safe cancellation and verified current-owner attachment have distinct methods. Genesis methods require the actual recent node-operator token and use admin routes; no genesis cancellation is exposed.

DEX lifecycle APIs use decimal strings for every economic amount and durable quote/request identities. Save the exact admission command before calling startDexSwap and replay it unchanged after response loss; the SDK never invents a new key or consent. External signing disclosure is potential egress, signature upload does not authorize server submission, and withdrawal cannot recall prior sends or free accounting. Only the durable Completed state and immutable settlement receipt establish a finished swap. Cleanup has separate participant consent and a separate persisted request key. Legacy swap methods remain pending the full provider/UI cutover; additive methods do not imply advertised readiness.

Client DEX adapters are retired (round 4): `DexAdapter`, `JupiterAdapter`, `TinymanAdapter`, the
`azoa-sdk/dex` entry point and the Tinyman peer dependency are gone. Swaps go only through the
node's durable swap lifecycle (`startDexSwap` and friends); the SDK never builds a swap itself.

URL ids are validated by shape before interpolation with one pair of helpers: `assertHex32`
(NFT lifecycle reference, DEX quote, deployment operation) and `assertHex64` (swap, WTT operation
and stage, hold command/mandate/record); UUID routes use `assertUuid`. A malformed id throws
`INVALID_INPUT` before any request leaves.

`SdkError.serverCode` carries the server's typed refusal code (`AZOAResult.code`); `isKybRequired`
recognizes the D-6 `KYB_REQUIRED` refusal (code, or the `TENANT_FORBIDDEN: KYB_REQUIRED:` message
the custodial routes use) so a page can explain business verification instead of a bare 403.

Round-4 route contracts (plan §4.9) have typed methods: KYC session/status/refresh and the operator
hosted-session refresh; wallet ownership challenge/proof; D-3 signing plan, `submitSignedOperation`
(re-read status afterwards — a 200 is the reconciled state, not a promise of success) and the stuck
`AwaitingSignature` operator list/resolve; deployment operator list/resolve; payout `resolveFiatPayout`
(`record-paid`/`return-units`) and `resolveFiatReversal`; hold liabilities and reserve assets;
retired exchange rows; wallet-identity conflicts; DEX settle-review; WTT operator inspect/actions and
stage keys; and `reconcileParkedRuns` returning `{ scanned, results, errors, hardStuck }` — zero
scanned or any error is never a success.

§tenant-payout-float — `getTenantPayoutBalance` (tenant) and `getTenantPayoutFloats` (operator)
return one `TenantPayoutFloatView` per currency/environment, mirroring the D-8 implementation;
`startTenantPayoutTopUp` returns the Checkout intent. `deficitMinorUnits > 0` means a charged-back
top-up is uncovered and every tenant-priced cash-out is refused until it is.

## Errors and credential exchanges (live QA 2026-10-18)

A 401 from a credential exchange (`/api/user/login`, `/register`, `/auth/challenge`, `/auth/verify`,
`/claim`) is a refusal and never triggers `onTokenRefresh`; login and register are also sent
anonymously, so a stale token never rides along. Only an authenticated route's 401 refreshes once.
`parseErrorBody` reads `message`, then `error`, then ASP.NET `ValidationProblemDetails.errors`, then
a string `detail`; the server text is kept on `SdkError.serverMessage`, and `userFacingMessage()`
is the copy for people (no method/path prefix). API key `scopes` cross the wire as a CSV string and
are normalized to a list at this boundary. `GET /api/fungible-creation` is a bare array.

## §api-key-in-browser

A tenant API key is a server-only credential: in a page or a worker it ships to every visitor. The
`AzoaApiClient` constructor therefore **throws** when `apiKey` is truthy and the runtime is a browser (a
`document`, or a `WorkerGlobalScope` with `importScripts`) or React Native (`navigator.product ===
"ReactNative"`: no `document`, and a key in an app bundle is just as extractable), unless the caller passes
the loudly named `dangerouslyAllowApiKeyInBrowser: true` (a trusted, non-public runtime: an internal kiosk, a
test harness). Server runtimes (Node, Deno 2, Bun, edge workers without a `document`) are unaffected.

The constructor **copies** its config, so a key assigned to the caller's object after construction can
neither slip past the guard nor change what is sent. `hasApiKey()` uses the same truthiness
(`Boolean(apiKey)`) `fetchWithAuth` uses to attach `X-Api-Key`, so the two can never disagree (a non-string
truthy key from runtime JSON used to be sent while `hasApiKey()` said false). `createDirectMoneyTransport`
still refuses a keyed client as defense in depth (`src/money/AGENTS.md` §direct-transport).
