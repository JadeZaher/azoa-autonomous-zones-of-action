---
type: Module Notes
title: Services/Reserve — the treasury-rate exchange and the buy-back that lets people sell
tags: [reserve, exchange, buy-back, liquidity, queue, W5.1, W8.2]
---

# Services/Reserve

The platform unit, the project treasuries it moves between, and the policy that decides whether
one exchange may happen at the rate its consumer declared. AZOA never computes a rate (D7): a
policy either admits the declaration or refuses it.

## §three-domains — reserve vs. treasury vs. destination (OI-81)

Three things share the word "treasury" in this codebase and must not be conflated:

1. **The platform reserve** (`IPlatformReserve`, `PlatformReserveService.cs`) — the one
   operator-owned custodial account every unit a sponsor or worker ever receives FROM. It
   holds the platform unit and every project asset, is the manager/reserve/freeze/clawback
   authority on assets this node creates, and pays the fees. It keeps NAMED PARTITIONS per
   asset (`contributor`, `investor`, tenant-declared) so an oversubscription check is
   possible on the node — see `ReserveDrawAuthority.cs` for who may move a partition's
   counters, and note the reserve owns no separate secret: its key is the per-chain
   platform key, so a chain with no configured platform key resolves NO reserve address.
2. **A project treasury** (`IProjectTreasury`, `ProjectTreasuryService.cs`) — one project's
   OWN custodial account, the account project funding actually moves TO. A treasury-rate
   exchange moves the platform-unit leg sponsor → platform reserve and the project-asset
   leg reserve → project treasury; without a treasury the project receives nothing on
   chain and its balance would exist only as rows in a tenant's own database — exactly the
   ledger-of-record shape D47 rejected for stakes. A buy-back is paid FROM this account,
   which is what makes it the project's promise rather than the platform's (§buy-back
   below): the platform reserve only ever backstops one with a reversible HOLD, never a
   transfer, until the sell actually settles.
3. **`NodeTreasuryDestination`** (`NodeTreasuryManager`, `Persistence/SurrealDb/Models/
   NodeTreasuryDestination.cs`) — NEITHER a reserve nor a treasury. It is routing policy:
   the versioned destination address, per chain/network, that the NODE OPERATOR's own
   governance-fee settlements are sent TO ("routing policy, separate from fee pricing"
   per its own `SurrealNote`). It holds no balance, backs no draw and funds no buy-back —
   it only says where the node's own fee revenue lands. `IProjectTreasury.cs` carries the
   same warning inline: "Not to be confused with `NodeTreasuryManager` /
   `NodeTreasuryDestination`, which are where the NODE's own fees land. Those belong to
   the operator; these belong to the projects."

Conflating (1) and (2) reads as "a project can be recapitalised from the platform reserve
directly" — it cannot; only a backstop HOLD can, and only a SETTLED sell converts it
(§buy-back). Conflating either with (3) reads as "the node operator's fee address is a
pool of user funds" — it is a destination the node itself is paid to, nothing more.

## §buy-back — what makes SELLING possible at all

`TreasuryRateExchangePolicy` refuses every SELL unless an `IExchangeLiquidityPolicy` is in the
container **and** that policy admits the sell.
`BuyBackLiquidityRegistration.AddBuyBackLiquidityPolicy` is the only thing that registers one,
and `Exchange:BuyBack:Enabled` defaults to false. The path is therefore closed twice over, and
opening it is a deliberate operator act rather than a deploy side-effect.

**Treasury first.** A sell is paid from the PROJECT's own treasury. That is what makes a buy-back
the project's promise rather than the platform's: a project that cannot afford to buy its units
back has not quietly borrowed the platform's reserve to look like it can. The bounds are
per-request (`MaxUnitsPerRequest`) and proportional (`MaxRequestShareOfTreasuryPercent`), over a
`TreasuryFloorBaseUnits` the treasury may not be drained below — so one large seller cannot empty
the pot ahead of everyone who comes after.

**The backstop is a declaration first, and a movement only on settlement.** When the treasury is
short, the promise made to a queued seller is a HOLD on the platform reserve's declared partition
(`ReserveToTreasuryBuyBackBackstop.ReserveAsync`) — visible, bounded by
`MaxBackstopUnitsPerRequest`, and reversible. Declaring rather than transferring is what stops the
platform becoming the counterparty of record for a promise it never made.

Only a sell that actually SETTLES converts that hold into a real reserve->treasury transfer
(`TopUpAsync`). A project that has already handed a seller their units has been recapitalised in
fact, and a partition that says otherwise is a number nobody stands behind.

**Head-of-line FIFO.** When even the backstop cannot fill a sell, the seller takes a PLACE rather
than a refusal, and is told the place. The queue is strictly first-come: a later-but-larger seller
never jumps, because the whole point of a queue is that a person who waited is not overtaken by a
person who waited less. A place is keyed on the exchange's own idempotency correlation, so a retry
re-reads its own place instead of taking a second one. A PREVIEW never takes a place — a quote
that queued would let a browsing user hold a slot they never meant to use, which is why
`ExchangeRateEvaluation.IsPreview` is load-bearing and not a hint.

**The context is required, and its absence fails closed.** The five trailing values on
`ExchangeRateEvaluation` (tenant, project reference, seller, request key, preview flag) are
optional only so the record stayed source-compatible with the call site that predates them. A
liquidity policy that cannot see the treasury a sell would be paid from REFUSES rather than
guessing — see `BuyBackLiquidityPolicy.ContextMissingMessage`. `ReserveExchangeManager` passes all
five; anything that constructs an evaluation without them gets a closed door, not a default.

**Known asymmetry.** The queue place is retired at ADMISSION (`ExchangeRatePolicy` `CloseAsync`,
~line 1049), not at settlement. A sell that is admitted and then fails to settle loses its place
and re-enters at the back under a new key. Moving the `CloseAsync` call to settlement success
would be strictly fairer, and belongs with whoever owns `Managers/ReserveExchangeManager.cs`.

## §three-outcomes — a movement the rail took and never confirmed (durable top-up, round 4)

`ReserveToTreasuryBuyBackBackstop.TopUpAsync` moves reserve units to the project treasury over the
same exact-saved-wire primitive the off-ramp uses (`IValueTransferTransactionModule` +
`IValueMoveReceiptStore`): claim `"{key}:move"`, prepare and save signed bytes once
(`value:{key}:{generation}`), then only observe or rebroadcast those bytes. The legacy
`provider.TransferAsync` + pending-marker path is gone.

| Outcome | What the reserve does | What the seller keeps |
| --- | --- | --- |
| preparation refused, or authoritative `FailedOnChain` | claim `Failed`; the hold stands and MAY be handed back | place + promise |
| saved bytes not yet confirmed | claim stays `InProgress`; the hold stands and may **NOT** be handed back | place + promise |
| `Confirmed` | hold → issued, project treasury credited, claim completed with the tx id | the fill |

A retry of the same request (the seller re-asking, or the queue re-evaluating the place) resumes the
in-progress claim with the SAME saved bytes, so one promise can never sign two transfers.
`ReleaseReservationAsync` resumes an in-progress claim first: if it has landed the set-aside is
spent (nothing is handed back); otherwise any `InProgress` claim — and any legacy claim failed with
`AmbiguousMoveReason` — refuses the give-back, because the units may already be the project's.
A move claim that failed for any other reason stays failed: one movement per request, an operator
resolves the rest. Held at the counters by `tests/AZOA.WebAPI.Tests/Exchange/BuyBackQueueTests.cs`
(`A_broadcast_that_never_confirmed_resumes_its_saved_bytes_and_settles_exactly_once`).

## §durable-exchange — journal before commitment, settle only the saved group

`ReserveExchangeManager.ExchangeAsync` runs every refusable gate before it writes anything. It then
journals the exchange as an immutable `ExchangeHoldSource` **before** any commitment or signature:

1. **Resume, never re-decide.** The source id is derived from (tenant, API key,
   Idempotency-Key). If that source already exists, the request resumes it. A request whose
   canonical terms (`RequestJson`) differ is refused (`OperationNotAllowed`). A replay never
   prepares or signs a second time.
2. **Gates.** In order: node governance, the platform unit, the rate policy (including buy-back
   admission, §buy-back), the hold window and the release request. A preview stops here. It
   journals nothing, and it returns the release disclosure when automatic release is requested.
   A preview reads the participant's identity readiness (and, with a mandate, their wallet), so the
   controller runs the live consent check on EVERY preview (round 4; previously only mandate
   previews, which left a per-person status oracle).
3. **Authority.** The controller captures the actual API-key credential
   (`ExchangeCallerContext.Credential`). `ExchangeAuthorityFactory` binds it together with the
   participant's watermark, wallet and `UserExplicit` swap:sign grant. Both parties are re-fenced
   at commitment, at preparation and at first egress. Evidence and settlement need no live
   authority.
4. **Commitment pre-check.** The partition room or the treasury counters are explained before
   admission. This check is advisory; the source transaction rechecks everything atomically.

The source then advances through `ExchangeHoldSourceService.ContinueAsync`: commit, begin
preparation, module `Prepare`, save the prepared candidate, authorize first egress, then
observe or submit. Each commitment kind is exactly one shape, and its accounting is atomic:

| Kind | Commit | Confirmed | Proven absent |
| --- | --- | --- | --- |
| Held buy (`Opening`) | hold `AwaitingSourceSettlement` + partition `Hold` (+ release mandate) | hold `Held` + treasury `Credit` | partition `CancelHold`, mandate cancelled |
| Direct buy (`Units: Issue`) | partition `Hold` | `ReleaseHold` + treasury `Credit` | `CancelHold` |
| Sale (`Units: Return`) | treasury `Hold` + return validity | treasury `ReleaseHold` + partition `Return` (clamped to outstanding) | treasury `CancelHold` |

**Recovery.** A submit that throws `HttpRequestException` leaves the source `Submitted` with its
saved bytes. `ExchangeSourceReconciliationWorker` then picks up due sources (`NextReconcileAt`).
For each one it observes, rebroadcasts the identical saved bytes, and projects finality. A source
idle in `Committed`, `Preparing` or `Prepared` for longer than `Exchange:UnsentSourceAbandonAfter`
(default 1h) is `Abandoned` with an `Unsent` verdict, and its commitment is released. This is safe
because no bytes were ever authorized for egress, and authorizing egress is a CAS from `Prepared`
that competes with abandonment. The worker never commits, prepares or authorizes anything.

**Release disclosure.** Automatic release needs the previewed disclosure to be echoed back
verbatim. The server checks it against fresh mechanics and captures transfer:sign from the same
key. Under the same key, an expired disclosure can be revised only while the source is still
`Admitted`. Each revision is a versioned, audited change (`ReviseExchangeReleaseAsync`).

## §d7-activation — the provider's declaration is the only pin

D-7: the exchange settles only where the provider advertises `IExchangeTransactionModule` for the
node's settlement network (`IBlockchainProviderFactory.DefaultNetwork`). `ReserveExchangeManager`
asks the provider before the quote and before any journal row (step 6b), so an undeclared network
refuses with `RAIL_UNAVAILABLE` and leaves no `Admitted` source behind;
`ExchangeHoldSourceService` repeats the check before every advance. Per-network pins live with the
declaration in the provider lane (L5): advertise the module only on a network whose fixture passed.
Ethereum and Solana parity is `round3-value-custody-parity-amendment.md`. Configuration:
`Exchange:MaximumGroupNetworkFeeBaseUnits` (blank refuses every exchange),
`Exchange:UnsentSourceAbandonAfter`, `Exchange:MaximumRecipientFundingBaseUnits`.

## §recipient-readiness — acceptance folded into the exact group

A direct buy delivers the project asset to the participant; a sale delivers the platform unit.
The participant's account must accept that asset first. Round 3 did this with the synchronous
`ICustodialRecipientPreparer` before admission (signed and sent outside any journal). Round 4
folds it into the exchange itself: the manager only READS readiness
(`IRecipientPreparationModule.GetRecipientAssetReadinessAsync`) and freezes the result into the
immutable source as `ExchangeUnitsCommitment.RecipientFundingBaseUnits` (null = already ready).
`ExchangeHoldWirePlan` turns it into `ExchangeWireIntent.Readiness`, and the provider prepends
`[reserve → participant native funding (if > 0), participant asset acceptance]` to the SAME atomic
group as the legs. Readiness therefore inherits everything the group has — saved exact bytes,
rebroadcast-only recovery, absence proof after the validity window, compensation on absence — and a
funded-but-unaccepted or accepted-but-unpaid half-state cannot exist.

Bounds: funding ≤ `Exchange:MaximumRecipientFundingBaseUnits` (default `"0"` in code: an unready
account that needs funding is refused with `RECIPIENT_NOT_READY` before any journal row;
`appsettings.json` ships `"201000"`), and the provider's own ceiling (1 000 000 base units)
whatever the option says. The provider accepts readiness only for the participant who signs one of
the legs, with that leg's exact signing context, so the reserve can never be made to fund an
arbitrary account. A blocked (frozen) holding refuses with `NOT_PERMITTED`. Held purchases need no
readiness here: the units go to the reserve; the later release runs Hold's own acceptance stage.

## §retired-rows — operator resolution, chain truth decides

Operation rows written by the old post-submit path carry no saved wire and no egress
authorization, so nothing settles them from the row alone. An operator lists them
(`GET /api/exchange/operator/retired-rows`, Operator policy) and records ONE immutable attestation
per row (`POST …/{operationId}/resolve`, `exchange_retired_resolution`, record id = operation id):
`Confirmed` or `Failed`, a reason, the operator, and the row's observed `TxHash`. The attestation
moves no value and no counter — it states that the operator made the node-side ledger good (or
verified nothing was owed). `ExchangeSettlementReconciler` returns true only when generic
reconciliation's own chain verdict equals the attested outcome AND the transaction hash matches;
the transition to `Completed`/`Failed` stays reconciliation's CAS. An identical replay returns the
original; a different second attestation is a conflict.

## §vocabulary

Nothing in this directory's consumer-facing text may name a chain, a token, a wallet or a gas
fee. A person selling their stake reads about *units*, *funds*, a *project's treasury* and a
*place in the queue*. The rail words stay in the provider layer and in operator logs.

## Fungible issuance projection

`ReserveAssetRegistrationPlan` is the shared pure registration builder. The durable fungible lifecycle composes that plan with original issuance authority and optional Holon attachment in one datastore transaction. The reserve store lives under Providers/Stores/Surreal and changes only partition counters/replay keys through its version CAS. Re-registering an identity with different supply, authority address, tenant or protocol is a conflict, not a successful replay.

## Platform genesis authority

Genesis claims a single immutable definition per network before admitting the fungible lifecycle. Its purpose witness is distinct from ordinary signing authority: actual recent node-operator credential, bootstrap revisions, frozen definition/key, reserve and protocol identity. It grants no user-wallet access and fabricates no KYC evidence. Quote consent precedes new signatures. A fresh operator session renews only that exact operation's authority; original command and signed bytes remain immutable.

Platform activation joins finalized authority, reserve registration and creation completion in one transaction. The generic platform store may update pending diagnostics, not activate or rewrite the definition. Confirmed accounting and already-authorized exact-wire recovery survive operator session expiry. Operator polling never requests faucet funds; funding is an explicit prerequisite. Genesis has no ordinary cancellation endpoint: withholding continuation pauses new effects, while already-authorized bytes may still settle. Proven no-effect replacement retains the original definition and archives the prior wire and evidence before requesting a new quote.

Initial definition claims join the same actual recent operator/bootstrap authorization fence as later genesis admission; actor IDs alone cannot freeze definitions. Active reads require a completed exact creation operation plus immutable authority, projection and reserve identity. An old active row with only asset text returns `PLATFORM_TOKEN_EVIDENCE_REQUIRED`; neither replay nor status may issue a replacement asset. Repository seeds contain no legacy platform-token issuance. Old mocked tests are not chain provenance and must migrate to explicit durable receipts.

## Reserve service invariants

Configured chain restrictions and public expected-address checks run before custody. An empty chain list uses the explicit shipped fallback; it does not allow every provider. Platform reserve signing requires a platform signing context. CallerWallet fungible creation now uses the separate durable lifecycle with the caller's actual wallet authority, rather than a platform signature over a user sender.

Partition declaration and draw apply the same ledger-owned tenant policy. Declaration is a set operation, cannot shrink below existing promises, and never accepts settlement authority to name new partitions. A draw requires a declared partition. The 128-entry per-asset FIFO replay window is bounded: evicted keys can execute again, so every economic caller requires its own durable command/receipt anchor. Malformed partition accounting is a refusal, not zero availability.

Public-address derivation catches only expected argument/format/cryptographic validation failures and returns a value-free refusal. Unexpected exceptions bubble to the centralized boundary. Secret inputs are never included in authored diagnostics. Reserve models and service comments carry terse contracts; rationale is maintained here.

## §declaration-authority — budgeting is stricter than drawing (round 4)

`ReservePartitionScope.RefuseDeclaration` gates partition declaration and the partition read
(`PlatformReserveController` GET/POST). It is `Refuse` plus one rule: the node's own unit (an
untenanted reserve row) is budgeted only by the node operator. `Reserve:PlatformUnitDrawPolicy`
(`DeclaredTenants`, `AnyAdmittedTenant`) widens who may DRAW the platform unit; before round 4 it
also let those tenants re-declare (and read) its partitions, i.e. re-budget the operator's supply.

## §treasury-links — `project_treasury_ledger` user links (SQL-04)

`tenant_id` (`record<user>`) and `closed_by_user_id` (`option<record<user>>`) are written as
`SurrealLink.ToLink("user", …)`; a bare hex id is rejected by 3.x, which made every treasury open
and close fail. The record id still hashes the bare tenant hex (`ProjectTreasuryLedger.RecordIdFor`),
reads go through the record id, and `ProjectTreasuryService.ParseTenant` accepts both link and bare
forms, so `GuardRowOwnership` compares GUIDs, never strings. Admin wire views that echo
`row.TenantId` now show the link form (see the lane record's cross-lane note).

## §simulated-project-treasury — per-project sim: accounts

`ProjectTreasuryKeyDerivation` registers `Simulated` with the same HKDF-SHA256 rule as Algorand
(parent = the Simulated platform key from `AZOA:Simulated:PlatformSeed`, info =
`chain|tenant|project`, 32 bytes out). Any 32 bytes are a valid simulated key, and the address goes
through `PlatformReserveAddressDerivation` → `WalletKeyService.SimulatedAddressFor`, so a project
treasury is an ordinary `sim:simulated:<base32>` address the Simulated provider validates — one
address rule shared with the reserve and simulated wallet rows. No new seed, no new config key.

**Gating** mirrors Services/Custody/AGENTS.md §simulated-platform-key: the registry is static and a
live node fails closed twice over — `GuardReference` reads `Reserve:Chains` (shipped: Algorand only;
the Development overlay adds Simulated), and the custody door refuses without the seed, which only
the Development config sets.

## §reattribution — tenant-funded cash-outs return units to the tenant (round 4, D-8)

Owner decision (b): units collected by a tenant-funded cash-out belong to the tenant that paid to buy
them back. `IPlatformReserve.ReattributeReturnAsync` applies, in one ledger CAS and under the same
replay window as a draw: source partition `Return amount` and `Declared -= amount` (its availability
and promises are unchanged), target partition `Declared += amount` (created on first use). Total
declared supply is unchanged, so the supply cap still holds. Only `ReserveDrawAuthority.NodeOperator`
may call it (the off-ramp rail; both partitions come from node configuration and a node-written row).
The target partition is `tenant-payout:{tenant compact id}`; the cash-out row records it in
`collect_partition`, and returning units draws `Issue` from that same partition.

## §receipt-operation-id

`ExchangeReceipt.OperationId` is set for every persisted exchange source (any rail, any state) by
`ExchangeHoldSourceIdentity.OperationId(source.Id)`: the first 16 bytes of SHA-256 over
`"azoa-exchange-operation-v1:" + sourceId`. The durable source replaced the retired `BlockchainOperation`
row, so no rail stamped one; consumers (ArdaNova's settlement gateway) require a non-empty Guid with
`isTerminal` and `requiresReconciliation == false`. It is derived at read time, never stored, so sources
admitted before this existed report the same id with no migration. A preview has no source and stays null.
