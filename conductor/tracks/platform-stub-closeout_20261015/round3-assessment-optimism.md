---
type: assessment
perspective: optimism-and-sycophancy
status: independent static assessment; no runtime gates run
---
# Round 3 full-goal assessment

The implementation now has substantial reusable machinery, but the original all-stubs goal is not complete. The right next step is focused integration and recovery work, not another rewrite and not relabeling safe refusals as finished capabilities. This assessment independently challenges both unsupported success claims and unnecessarily pessimistic deferrals. Lane A/B final review corrections were still arriving during this snapshot; their completed handoffs must be reconciled before the next plan is frozen.

## Evidence boundary

Source, handoffs and targeted callsites were inspected. No build, test, typecheck, schema application, funding, public-chain transaction or vendor workflow ran in this assessment. Local `main` is the only listed worktree, at `bb512cf` in this snapshot, with roughly 625 changed/untracked paths. Consolidating worktrees is real progress; that is not clean committed/pushed main. Authored local protocol fixtures are stronger than mock-only tests but still not passing protocol evidence.

`deferred.md` currently contains only Identomat workflow dashboard setup and the precise absence of a vendor idempotent-create/authoritative-absence contract. These remain properly narrow external items. Their prose must use ?implemented in source? until the integrated sweep passes. Everything below is required implementation or verification, not an external deferral.

## What exists and should be reused

1. Identomat KYC/KYB already has real adapters, shared authenticated callbacks, per-attempt admission, verified correlation attachment, active-attempt authority fences and subject-bound AML lineage. Veriff and versioned generic hosted protocols now reuse those boundaries; wallet auth supports concrete EIP-191/Ed25519 proofs and canonical identity migration. Rebuilding identity would discard useful concurrency work. Run actual vendor workflows only when configured, while exercising real HTTP contract fixtures and persistence races locally now at the scheduled final gate.
2. Stripe source distinguishes Connect transfers from bank payouts, keeps account-scoped provider correlation, preserves reversal debt and uses durable refund/replay/reconciliation paths. The DEV key has been authenticated by root and a dev listener secret exists in ignored local configuration. There is a practical bounded DEV verification path; ?no webhook secret? is stale and must not become a deferral. No test payment or bank payout success has yet been demonstrated.
3. NFT lifecycle has real chain modules for all three advertised chains, immutable fee consent, prepared-wire journals, participant acceptance, issuer authority, refund/revoke provenance and a restricted observation worker. SDK/UI server discovery exists. Complete the action adapters and actual local protocol gate rather than replacing these with DB ownership writes or mock transaction IDs.
4. Action execution has canonical immutable inputs, domain references, transaction-fenced local effects, pending reconciliation, immutable workflow admission and occurrence completion boundaries. Governance has current authority/evidence fences and immutable finalization. These are reusable admission/recovery primitives for DEX, bridge and deployment cleanup. They are not proof that every registered handler already consumes those primitives.
5. Native runtime deployment now produces hash-pinned loaders and an actual caller-authorized frozen ActionRun. Native chain modules construct real typed wires and preserve exact custody/preparation identity. The root/child bundle and Holon authority fence is stronger than previous pre-read-only checks. Keep this architecture and close the remaining partial-stage/consumer semantics.

## Prioritized concrete work

### P0: finish settled economic outcomes, not only transaction construction

`Services/Dex/TinymanDexAdapter.cs::BuildSwapTransactionAsync` still returns success with instructions to construct a group; it does not construct the promised group. Its quote uses fixed 30bps rather than an explicit fresh pool fee contract, parses asset IDs as uint, and maps Devnet to a public testnet app ID by default. `sdk/azoa-wallet/src/dex/tinyman.ts` already invokes the official SDK transaction generator: reuse its contract/fixtures and captured official Tinyman source, not its output blindly. The server needs exact pool/app/genesis/asset/amount/min-output binding, current fee/reserves, canonical group construction and actual signing/receipt recovery. Read-only quote unavailability remains a normal environment condition; a fake successful build does not.

Jupiter has real `/quote` and `/swap` transport, but that is a transaction-builder integration. `Services/Actions/Handlers/SwapNodeHandler.cs` calls the unsigned builder and returns node success. The dashboard explicitly stops at an unsigned descriptor with ?wallet adapter? still required. This is honest UI wording, not completion of a usable settled-swap journey. Add an explicit build-only contract where that is truly the promised result, plus the actual authenticated sign/submit/observe journey and durable action domain for economic swaps. Validate returned Solana instructions/accounts/programs, fee limits, wallet, mint/amount/minimum output and network before accepting remote-built wire; do not sign arbitrary aggregator bytes.

`Program.cs` registers Jupiter and Tinyman only; no concrete Ethereum DEX adapter was found. An enabled Ethereum chain plus a refusing provider wrapper is not Ethereum swap support. Implement a reviewed concrete router/quote protocol with explicit supported networks and bounded local protocol fixtures. Reuse Ethereum ABI, chain pins, nonce allocation and exact-wire journaling. Do not infer permission to deploy public infrastructure or use live keys.

### P0: bridge source publication and target settlement remain missing

`Services/Bridge/WormholeProtocol.cs::ObserveAsync` explicitly refuses every supported route because source emitter sequence extraction is unimplemented. Wormhole guardian signature verification, replay digest and bridge ledger are valuable existing work; they cannot compensate for absent actual source publication/observation and target redemption capabilities. Native contract call capability alone is also insufficient unless the exact bridge contract/program/payload and ownership semantics are bound.

Implement source publication with authoritative emitter/sequence extraction from the confirmed transaction, then exact VAA asset/recipient/amount/source binding and actual target settlement through durable prepared receipts. Preserve consumed-proof uniqueness and reverse/refund semantics. Use official protocol contracts and local deterministic guardian/program fixtures; lack of a live guardian service must not excuse missing codecs or provider modules. Route catalog/UI and action Bridge/Back must match implemented capabilities, with pending proof/finality visible rather than false success.

### P0: Solana partial deployment failure is not terminal cleanup

`DeploymentService.Recovery.cs` currently can mark a later native FailedOnChain stage terminal while prior CreateBuffer/Write stages have locked rent. `GetNextStageAsync` has no complete cleanup outcome contract yet. A later RemoveAuthority failure can also follow a successful deployment/upgrade. Reporting Failed and allowing a fresh deployment would hide real deployed state or stranded resources.

Add durable cleanup stages only after authoritative final/no-effect evidence for the original generation. Bind exact buffer, original authority, refund destination and returned lamports. Preserve successful deployment/upgrade truth while authority removal is recoverable. Reserve cleanup costs in the original quote, and provide an explicit separately quoted linked recovery operation if retries consume the original cap. Never silently enlarge old caps, sign another generation after ambiguity, or describe estimated rent as refunded. This is bounded protocol recovery work, not an implementation-size deferral.

### P1: remaining action coverage is a real integration task

At this snapshot `BlockchainExecuteNodeHandler` still reads `GetAsync(cfg.Id)` without actor scope and treats every nonerror operation as success. The generic manager now exposes authoritative deployment receipt projection, but its deliberate refusal of action-bound fresh native admission is not closure of this handler. Bind actor/tenant and original operation identity, distinguish real terminal result from all pending states, and route fresh native effects through the saved deployment domain envelope. Lane B already identified this seam; reconcile its final patch before calling it a new unresolved defect.

Do a registry-to-resolver coverage test over every advertised handler, not only a fixture for the new abstract envelope. Swap, bridge, hold/value and external delivery each need their actual resume contract. A permanently ambiguous domain without an authenticated evidence/recovery journey is unfinished. Conversely, genuine external HTTP receiver exactly-once cannot be promised; preserve stable event IDs and documented at-least-once behavior.

### P1: AVM identity/precision and compiler claims

`Models/Deployments/NativeArtifacts.cs` uses ulong foreign application/asset IDs while the SDK exposes number arrays. That loses precision above 2^53. Adopt strict decimal-string wire IDs or reject values outside the exact JS-safe range at both boundaries before signing. Extend the same inventory to Tinyman uint IDs and Algorand metadata's signed-long parsing; consistent asset identity is a cross-surface concern.

`AlgorandProvider.NativeArtifact.cs` compiles TEAL remotely but retains caller-supplied CompilerIdentity. Record actual verified/configured algod build/protocol identity for server compilation, and label uploaded bytecode/ELF provenance as caller-declared unless proven. Compiled byte identity remains mandatory. Mutable AVM direct history is implemented; inner mutation histories currently return ambiguity. Do not market general exact-code proof as complete until supported nested execution histories have a concrete verified path, or clearly expose the precise supported protocol subset while implementing the remaining advertised path.

### P1: metadata and runtime resource parity need targeted closure

`SolanaProvider.GetTokenMetadataAsync` returns supply/decimals only and substitutes zero for absent RPC value. It does not read the Metaplex metadata the newly implemented mutation modules write. Reuse the existing metadata PDA/codec, expose authentic metadata and authority/mutability when present, and distinguish absent/unreadable supply from authoritative zero. Algorand metadata returns actual indexer fields but retains signed asset parsing and catch-all exception translation; resolve those when this surface is touched.

`DeploymentService.Ecosystem.cs` directly appends nested DappSeries action snapshots and mappings without adding their extracted bound Holon references. `DappCompositionManager` has `ExtractBoundHolonIds` for standalone series generation. Reuse one shared extraction/validation helper and bind nested-series resources into the same immutable witness contract; do not assume source action hashes alone capture referenced resource content.

Public runtime invocation is supported server-side through current bundle visibility checks, but artifact retrieval and the deployment console are owner/discovery scoped. Verify a genuine second-user public invocation/download journey, not just an owner test. Keep caller authority and live resource guards; do not solve UI discoverability by granting the deployer's signing rights.

### P1: finish user/operator journeys and final evidence

The new identity, NFT, fiat and action operator SDK/UI methods substantially reduce the earlier parity gaps. Verify lost first responses, account switches, cleared browser storage, changed tenant, expired consent and cross-device server discovery. A local saved key is retry convenience, not durable discovery. Explicit zero fees and missing configuration must remain distinguishable. Check wallet login/link/claim for each advertised chain through the actual browser encoding, not only raw cryptographic vectors.

The final gate must generate new Surreal schemas, compile all projects/providers, run real persistence races, execute actual Algorand/Solana/Ethereum adapter protocol fixtures, test SDK/frontend and run bounded Stripe DEV callback/recovery journeys. AVM LocalNet setup still needs pinned official versions; A owns that correction. Root owns real .NET EVM adapter protocol fixture. A-authored Solana fixture Encoding ambiguity was found independently and sent for correction. None of these gates has passed yet.

### P1: keyed template authoring must precede convenient start

Late independent review from lane B/root found `fromTemplate.start` generated a fresh action on each retry, so reusing a key could still create duplicate economic runs because run keys were scoped per action. Root temporarily rejects that shortcut; explicit instantiate, persist action, preview and start remains available. This safe refusal is not completion of the advertised convenience. Implement an atomic keyed template-authoring identity bound to template version, exact parameters, actor and tenant; retries resolve the same authored action before workflow admission. Preserve separate immutable invocation identity. Likewise stale-preview rejection needs an authoritative proven-unadmitted outcome/tombstone before the panel can safely rotate a key; transport errors or apparent absence cannot unlock a possibly admitted request.

## Constructive parallel packages

- DEX package: authoritative quote identity + actual Tinyman group + validated Jupiter wire + Ethereum adapter, durable swap domain and SDK/UI signing/settlement. Freeze protocol/consent/economics contracts first.
- Bridge package: source publication/observation modules, exact proof binding and target settlement, durable reverse/recovery, action and client parity. Reuse existing guardian verifier/replay ledger and current provider preparations.
- Native closure package: Solana linked cleanup, AVM precision/provenance/nested-code evidence, shared nested resource extraction and public runtime journey. No unrelated rewrite of the action interpreter.
- Coverage/evidence package: reconcile final lane B handlers and UI, exact registry coverage, priority Identomat/Stripe DEV acceptance setup, schema and final gate inventory. Keep actual execution to the user-authorized end-of-work sweep.

Each package needs adversarial plan review and a separate source reviewer. Round 4 must reassess the combined platform again; it is not a ceremonial ?all done? check. Only after runtime remediation, independent verification and clean committed/pushed main can the session claim the original goal closed, with narrowly evidenced vendor-only items left in deferred.md.
