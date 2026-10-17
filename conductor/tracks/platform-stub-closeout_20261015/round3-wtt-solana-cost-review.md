---
type: Review
status: Changes required before fee-control implementation
---

# Solana WTT bounded-payer cost amendment review

Root independently reviewed the proposed bounded protected payer and the captured official transfer processor references. Keeping the real outer fee payer, source owner and message signer distinct, funding atomically, and resolving every authorized wire before cleanup are appropriate. Exact transaction replay and retained-resource recovery preserve economic identity.

Blocking correction: a payer funded with protocol fee plus rent bounds total user funding, but cannot separately bound spent protocol fee. A fee increase may consume unused rent when an account already exists or rent is lower than quoted. Unsolicited credits can also permit a fee above the frozen allowance. Thus the statement that a protocol cost increase necessarily fails is too broad. The design must enforce the exact quoted fee category at execution, or prove the already-consented cap explicitly accounts for the entire amount as potentially spent fees without relabeling retained-rent budget.

An execution-time guard against the exact pinned core configuration, in the same atomic transaction and protected by its account locks, is one candidate; it requires real program implementation, deployment identity, and protocol fixtures. Preflight remains insufficient. Added required cases: fee rises by one while still below total funding; reduced/existing-account rent; unsolicited credit. These cases must not exceed the admitted fee cap even if total user debit remains bounded.

Source review only; no runtime checks performed. This is an implementation correction, not an external deferral.

## Revised contract reread

The revision adds a concrete immutable on-chain guard for exact core fee, Rent bytes, program owner/loader links/code hashes and older deployment slots, with explicit transaction account locks. This addresses the separate fee/rent counterexample. Contract accepted for implementation with two clarifications: the guard is the first stateful instruction after any required ComputeBudget prelude, and exact fee equality rejects decreases too (residuals instead arise from lower/existing-account rent). The closed builder must exclude intervening config/upgrade instructions. Actual protocol fixtures must measure guard hashing compute, loaded-account and wire-size feasibility; no assumed cost. Acceptance remains contractual, not source/runtime or capability approval.
