# Items dropped or reshaped by the chain-first re-sequence

Every old item dropped or reshaped, and why. Unlisted old items (W0.4, W0.5, W1.2, W1.3, W1.5, W1.6, W2.1-W2.5, W6.1, W6.2, W7.1 core, W8.4) carry over unchanged in substance.

- **W0.1 — RESHAPED.** The §2/§11.1 amendment ("ArdaNova operates the node and custodies users") is WITHDRAWN (D48 corrected: the owner is operator and custodian; the contract's split was right). §3 flips from "ArdaNova owns Stripe in+out" to "AZOA owns the `Fiat.USD` rail scoped to ARDA on/off-ramp" (D45); line 75 moves webhook verification to AZOA. §11.2/§1/§4.1/§5.1/§7 amendments kept.
- **W0.2 — RESHAPED.** ArdaNova product.md/tokenomics now state ARDA as the user-facing currency, uniform KYC, stake = per-project ASA (D44/D46/D47) instead of "KYC tiered, ARDA internal, USDCa/Connect claims removed".
- **W0.3 — RESHAPED.** "Reconcile D27 vs D36" moved to the operator's clock (D48). D36 wording is still corrected as a doc-truth item. D9's sidecar bullet is marked superseded by D43/D45 rather than "fixed".
- **W1.1 — RESHAPED.** "Rail-aware eligibility: no walletId/walletAddress on the fiat path" DROPPED (D46: wallet required for every value action). Kept: unavailable-vs-not-ready, fail-open removal, client contract mirror.
- **W1.4 — RESHAPED.** KYC policy (trust profile, assurance level, policy version) becomes node-level operator config; the per-tenant provider PUT is an override, not the way policy is set (D48).
- **W1.7 — RESHAPED.** "Wallet stays skipped until Wave 8" DROPPED; wallet provisioning proceeds now (custody assumed, D48). Railway variables consolidated here (removed from W1.4's owns list).
- **W3 (old, fiat-first) — DISSOLVED.**
  - **W3.1** Stripe plumbing in ArdaNova — DROPPED; the rail lives in AZOA (D45) → new W4.1.
  - **W3.2** "credit TokenBalance + ledger row as the stake, AZOA anchor outbox dormant" — DROPPED (D47: stake is on chain from day one; there is no anchor-later) → new W6.2/W6.3 settle through the exchange.
  - **W3.3** "assetId optional, DB-ledger units allowed" — DROPPED (assetId required, D47). Its economy-mode banner survives as new W6.5.
  - **W3.4** "quote USD, remove ARDA rows" — REVERSED (D44: ARDA is what users see) → new W6.4 quotes ARDA with a USD reference and deletes the two-hop chain.
  - **W3.5** retire sidecar — KEPT but moved to AZOA (new W4.4); ArdaNova's dead `StripeService` handler is deleted with the whole file in new W8.2.
  - **W3.6** self-serve fundability with optional assetId — RESHAPED → new W5.2 mints the project ASA into the reserve at publish; owner must be KYC-ready (D46, Q-X4).
- **W4.1 (old)** reversal pipeline in ArdaNova + hold window on `TokenBalance` — MOVED to the AZOA rail (new W4.3, D45) and the hold window to the node's HOLD ledger (new W3.4, D47).
- **W4.2 (old)** fixed-scale money — KEPT (new W5.3) but "collapse ExchangeService to project-token → USD, ARDA internal" REVERSED (D44); ExchangeService becomes the declared ARDA↔project-token rate.
- **W4.3 (old)** KYC tiering enforcement — DROPPED (D46). Its second half (hold state instead of infinite Retry, attempt ceiling, alert) survives as new W6.2/W7.5 with ARDA/hold return in place of fiat auto-refund.
- **W4.4 (old)** backer privacy — KEPT as new W6.6 (amounts in ARDA).
- **W5.2 (old)** "reserve stake in the DB at accept; worker KYC gate moved to settlement" — RESHAPED → new W7.2 reserves via an AZOA HOLD from the contributor partition; the worker gate stays at accept (D46).
- **W5.5 (old)** USD out via Stripe Connect inside ArdaNova — RESHAPED → Connect lives in the AZOA rail as the ARDA off-ramp (new W8.1, D45/Q-X5); ArdaNova's cash-out is two hops (new W8.2).
- **W7.1 (old)** Money view — KEPT as new W10.1, now in ARDA with real D16 USD stamps and AZOA op ids on every row.
- **W7.2 (old)** node transparency SDK read + page — SPLIT: page/docs new W10.2, SDK methods new W10.3.
- **W8 (old, custody hardening) — REMOVED FROM THE SCHEDULE.** W8.1 (sign-only handle, envelope), W8.2 (CustodyPolicy, boot guard, health probe, float caps), W8.5 first half (KEK drill, platform-key rotation) → "Operator's own clock" appendix (D48). W8.3 (consent grant for child users) KEPT as new W3.6 — it is consent correctness, not posture. W8.4 (un-park reconciliation) KEPT as new W4.5. W8.5 second half (D16 `TryReadRailAmount` fix, TenantDeclared rate source) KEPT as new W4.6.
- **W9 (old, chain anchor) — PROMOTED, not dropped.** W9.1 split into new W3.5 (wire fix), W5.2 (mint job), W7.4 (credential issuance via the tenant credential). W9.2 → new W3.3. W9.3 → new W3.4. W9.4 → new W6.3 (the gateway wraps an exchange op, not an IssueUnits call from ArdaNova; Mint kinds retired). W9.5 → new W4.7. The "AZOA as a later anchor" framing is DROPPED: the chain is the ledger of record from the first sponsorship.
- **Review Wave 0 owner questions Q1-Q4** — ANSWERED by D44-D47 and removed; **Q5-Q7** — carried as stated assumptions. Replaced by five new decisions Q-X1..Q-X5 that D44 opens.
- **Review decision-to-ratify #2** (ArdaNova talks to Stripe directly) — REJECTED (D45). **#8** (ARDA internal-only) — REJECTED (D44). **#5** (Mint-per-reward) — REJECTED as the review recommended (D47). **#6** (uniform KYC + wallet) — the review said REJECT; D46 RATIFIES it. **#9** (no external wallets, treasury-rate exchange) — ratified; treasury-rate is now Q-X1's recommendation rather than an assumption.
- **Review 'dropped' list** — all 14 stay dropped, with two notes: "Tinyman AMM on ArdaNova's path" is now an explicit Q-X1 option (still recommended off); "D16 oracle inside AZOA" stays dropped, but the on-ramp now yields a genuine USD-at-time stamp because the fiat amount is known at the webhook.
