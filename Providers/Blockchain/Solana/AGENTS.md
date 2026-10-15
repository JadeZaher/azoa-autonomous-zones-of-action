# Providers/Blockchain/Solana

## §signer — SolanaTransactionSigner (final-hardening B1)

Real Ed25519 Solana signing behind the chain-agnostic `ITransactionSigner` seam.
Crypto comes entirely from `Solnet.Wallet` / `Solnet.Rpc` (already referenced) — no
hand-rolled curve math, base58, or wire framing.

### Seam contract — the exact byte format (B2 MUST match this)

This is the contract the not-yet-real transaction-building provider path (B2) has to
produce so `Sign` accepts its bytes:

- **Input (`canonicalTxn`)** = the serialized Solana **Message**, i.e. the output of
  `Solnet.Rpc.Builders.TransactionBuilder.CompileMessage()` (equivalently
  `Solnet.Rpc.Models.Transaction.CompileMessage()`). These are the exact bytes that
  are Ed25519-signed. `Message.AccountKeys[0]` MUST be the fee-payer / first required
  signer — the signer verifies the resolved key matches it and fails closed
  otherwise. NOTE: the blockhash is inside the message, so B2 must set a recent
  blockhash before compiling; the signer does not (and cannot) add one.
- **Output** = the submittable **wire transaction**: `[compact-u16 signature count]
  [64-byte Ed25519 signatures...][message bytes]`, produced by
  `Transaction.Populate(message, [signature]).Serialize()`. This byte-matches
  Solnet's own `TransactionBuilder.Build(signer)` for identical inputs (pinned by
  the round-trip test). It is ready for the JSON-RPC `sendTransaction` (base64/base58
  encode at the RPC boundary — the signer returns raw bytes).

### Key representation

The signer reconstructs the signing account from the raw 64-byte Solana secret
(`new Account(secret64, secret64[32..64])`) — the same representation
`WalletKeyService.GenerateSolanaKeypair` persists (Solnet `PrivateKey.KeyBytes`).
Fail-closed guards: a key that is not exactly 64 bytes, a message whose first
account key is not the signer's public key, a non-64-byte signature, or a final
transaction that fails `VerifySignatures()` all return an error result — never a
partially-signed or wrong-key transaction. The error message never contains key
bytes.

### Current state of the provider (B2 landed)

HISTORICAL NOTE, kept because the wording used to mislead: this section once said the
provider's Mint/Burn/Transfer were fail-closed because no build→sign→submit pipeline
existed, and the provider's own error strings blamed "a fail-loud stub" signer. The
signer was never a stub, and the pipeline now exists — see §spl-pipeline below. What
remains fail-closed is the bridge quartet (LockForBridge / MintWrapped / BurnWrapped /
ReleaseFromBridge), and it is fail-closed because no reviewed Solana bridge lifecycle
has been designed, NOT because of the signer.

`VerifyBridgeProofAsync` was removed entirely (the always-true verifier is gone; VAA
verification is the WormholeAdapter / Secp256k1VaaSignatureVerifier path only). NOTE:
the signer returns the fully-assembled submittable wire transaction (not a bare
signature) — the provider submits it as-is.

## §spl-pipeline — the real build→sign→submit path (Phase 2, step 10)

`SolanaProvider` now owns a genuine SPL pipeline. `MintAsync`, `BurnAsync`, and
`TransferAsync` build instructions with Solnet's program builders, compile the
canonical message, sign through the custody choke point, broadcast once, and poll to
confirmation. The former "no pipeline exists / the signer is a fail-loud stub" refusals
are gone — that claim about the signer was never true (see §signer).

### Byte contract (unchanged, now actually exercised)

`BuildSignSubmitCoreAsync` produces `TransactionBuilder.CompileMessage()` — the
serialized **message**, not a transaction — and submits `Convert.ToBase64String(...)`
of whatever the signer returned, byte-for-byte. Nothing is re-serialized after signing;
re-framing would invalidate the signature. The transaction id is **read out of** the
signed bytes (`Transaction.Deserialize(...).Signatures[0]`, base58) rather than
generated, so no code path can report a value move under an id the chain never saw.

### One signature per transaction

The signer seam signs with exactly one key, so every transaction is built with a single
required signer: the fee payer, which is also the mint authority (mint), the token
holder / burn authority (burn), or the sender / transfer authority (transfer). The
provider does not resolve that address itself; it uses the caller-declared address as
the fee payer and relies on the signer's `AccountKeys[0] == resolved public key` check
to fail closed if custody hands back a different key.

This is why a mint account is created with `SystemProgram.CreateAccountWithSeed` rather
than `CreateAccount`: a plain create requires the new mint's own keypair to co-sign. A
seed-derived address is authorized by the base account's signature alone. The seed is a
fresh GUID per call, so each `MintAsync` creates a distinct asset — the Algorand
ASA-create analogue — and the derived mint address is known *before* signing, so the
pending-confirmation message can name it for reconciliation.

### ATA reality

Solana does not create a token account implicitly. `TransferAsync` derives both
associated token accounts, refuses before broadcast when the **sender** has none
(nothing to send, and a preflight rejection would only cost a fee), and prepends
`CreateAssociatedTokenAccount` when the **recipient** has none, paid for by the sender,
in the same transaction. A transfer that "succeeded" into a non-existent account would
be a value-loss bug. `TransferChecked` (not `Transfer`) carries the mint and decimals so
the cluster itself proves both accounts belong to the mint.

Addresses passed to `TransferAsync`/`BurnAsync`/`GetBalanceAsync` are **owner** wallet
addresses; the provider derives the token accounts. Amounts are **base units**
everywhere, including the SPL branch of `GetBalanceAsync` (which sums every token
account the owner holds for the mint). Platform-created mints use 0 decimals, so base
units are whole tokens.

### Pending is not failure

A confirm-timeout returns a SUCCESS result carrying the real signature prefixed with
`OperationStatus.PendingConfirmationMarker`, so the operation manager records
`PendingConfirmation` + `TxHash` and reconciliation settles it from chain truth. An
on-chain `err` from `getSignatureStatuses` is a real failure and is reported as one.
Broadcast uses `RetrySafety.Broadcast`: only provably pre-broadcast transport failures
are retried, and the provider never re-builds or re-signs after a send.

`GetTransactionStatusAsync` treats a `getTransaction` result of `null` as an ERROR
(→ `ChainConfirmation.Unknown` → "park, do not act"). It previously returned a success
payload with `success = true` for an unknown transaction, because `meta?.Err == null` is
vacuously true when there is no transaction at all — a false CONFIRMED that could have
settled a transfer that never landed.

### Platform key: the one thing still fail-closed

`KeyCustodyService.WithPlatformSigningKeyAsync` derives its key from
`AZOA:Algorand:PlatformMnemonic` via the Algorand SDK, i.e. the shared platform door is
Algorand-shaped. Mint and burn arrive as platform ops, so with custody wired they hit a
32-byte key and fail closed with a message naming that cause (never a mismatched
signature, never a fabricated success). Two paths work today: per-user contexts
(`TransferAsync` from a user wallet), where custody returns the wallet's own 64-byte
Solana secret; and the no-custody fallback, which reads `AZOA:Solana:PlatformMnemonic`
and is denied in Production/Staging exactly like the Algorand fallback. Making the
custody platform door chain-aware is the remaining unlock for platform mint/burn.

### Constructor selection is load-bearing

`Program.cs` registers the provider with a bare `AddTransient<SolanaProvider>()`, so the
container picks the constructor. The 4-argument overload takes only root-resolvable
singletons and is the one DI selects, which is how the value path gets a signer without
touching `Program.cs`. The 6-argument overload exists for tests and is kept
container-invisible by requiring an `HttpMessageHandler` (never registered, no default)
— `IKeyCustodyService` is scoped, and injecting it into a provider the factory caches
for the app's lifetime would be a captive dependency.

### Still not implemented (deliberate, explicit)

Bridge primitives (`LockForBridgeAsync`, `MintWrappedAsync`, `BurnWrappedAsync`,
`ReleaseFromBridgeAsync`), Metaplex metadata, `CloseTokenAccountAsync`, and DEX
exchange/swap all return explicit errors. `CreateTokenAccountAsync` returns the real
derived ATA address when it exists and refuses otherwise, because that seam carries no
signing context or fee payer. None of these fabricate an operation id any more, and
none of them blame the signer.
