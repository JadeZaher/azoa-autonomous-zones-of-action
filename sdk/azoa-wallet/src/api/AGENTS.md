# API contracts

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

§tenant-payout-float — `getTenantPayoutBalance`, `startTenantPayoutTopUp` and
`getTenantPayoutFloat` are coded against the L3 D-8 design record before the routes exist. Field
names follow the design (`available/reserved/pendingCreditMinorUnits`, operator `consistent`); a
node without the routes answers 404 and callers must render "not offered". Re-check the shapes when
the D-8 implementation merges.
