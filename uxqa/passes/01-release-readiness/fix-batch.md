---
type: UX Fix Batch
title: Confirmed release UI fixes
description: Code-confirmed findings, fix scope and pending coordinator verification.
---

# Confirmed UI batch — ready for independent review

All application edits are in `.worktrees/node-admin-release`; root application source is unchanged. This batch was authored after the four recorded UI lanes. No tests, browser checks or axe reruns were executed during the fix batch. `git diff --check -- frontend/src` passed. Approval and coordinator verification remain pending.

| Finding and evidence | Confirmed source | Change |
| --- | --- | --- |
| Critical `button-name` on every zoom journey page; nameless banner control in accessibility snapshot (`zoom-slow-account.md`) | `frontend/src/components/layout/sidebar.tsx:233` was an icon-only SheetTrigger without a label | “Open navigation” accessible name, decorative icon hidden, minimum 44px target. The existing dialog retains its Navigation title and Base UI keyboard behavior. |
| Serious `color-contrast` across public, sign-in, dashboard and operator pages (all four lane reports) | `globals.css` used the same mid-orange primary with pale foreground in both themes; `landing/chrome.tsx` used `#C8501E` small text on paper; operator login used paper backgrounds while inheriting dark-theme pale label text | Light primary is deeper with pale foreground; dark primary is lighter with ink foreground. Paper landing accent changed to `#A63D15`; dark-card hover preserves opaque pale text. Operator login resets light tokens and explicit foreground. Palette calculations: old dark button pair ~4.03:1 vs revised ~7.11:1; old landing accent/paper ~3.89:1 vs revised ~5.45:1. Browser verification still required. |
| Serious `scrollable-region-focusable` on zoom Overview (`zoom-slow-account.md`) | `frontend/src/app/(dashboard)/layout.tsx:38` independently scrolling main region had no keyboard focus target | Added `tabIndex=0` and visible focus outline, allowing keyboard scrolling. |
| All default provider cards displayed a 1969 update (`operator-readiness.md`) | `Services/Kyc/KycProviderRegistry.cs:273` creates version-zero built-in defaults with UnixEpoch; provider page formatted that sentinel as an update | `providers/page.tsx:151` now says policy has not been configured yet for version zero. Persisted disabled policies retain their real update time. No API contract, data or enablement changed. |
| Two raw `Chain info failed: TypeError: Failed to fetch` banners on Overview (`keyboard-account.md`, `zoom-slow-account.md`) | `overview/page.tsx` returned ErrorBanner directly from each chain's SDK error, removing the card identity | Each named Algorand/Solana region remains visible with an unavailable-details sentence. Session status is independent; neither a backend cause nor a balance/node-outage claim is inferred. |

Meaningful regressions authored, not executed: `components/layout/__tests__/sidebar.test.tsx` opens the labelled mobile dialog and locates real navigation destinations; `app/(dashboard)/overview/page.test.tsx` checks per-chain fallback and returned data; `app/operator/(console)/providers/page.test.tsx` distinguishes untouched built-in policy from a persisted disabled policy.

The mobile locator ambiguity, gold settings timing, access-denied external requests and possible API/CORS causes are not application fixes in this batch. Readiness/permissions and fixture identities were preserved. Human-research hypotheses remain in the lane reports and still need five sessions per segment. A passing coordinator rerun must not be represented as completed human validation or a complete accessibility audit.
