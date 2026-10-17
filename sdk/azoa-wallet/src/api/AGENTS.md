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
