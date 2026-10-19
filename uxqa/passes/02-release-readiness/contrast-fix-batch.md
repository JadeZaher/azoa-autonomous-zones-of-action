---
type: UX Fix Batch
status: PENDING_COORDINATOR_RERUN
---

# Confirmed remaining contrast findings

A separate fixer probe ran at a 1280-pixel desktop viewport against the guarded
local E2E stack. Both plugin and adapter guards passed using the coordinator's
existing fake-vendor relay ports 29443/29444. It performed read-only navigation
and axe `color-contrast` scans; it did not sign in, submit forms, mutate roles,
call a model or count toward persona/gold-task success. Both sessions called
`finish()` in `finally` in probe mode. The aggregate pass report predates this
diagnostic and has not been rewritten as a passing verdict.

Sanitized selectors, computed styles and numeric axe checks are recorded in
`.omc/research/uxqa-pass02-contrast-nodes.json`; the diagnostic script is
`.omc/research/uxqa-contrast-diagnostic.mjs`. Cropped failing-element screenshots
and private traces remain in ignored `runs/*--contrast-fixer-diagnostic/` paths.
Screenshots containing token-like or email text were refused. No HTML, input
values, storage state, credentials or user-specific text was copied into the
diagnostic JSON.

| Routes | Exact finding | Source fix |
| --- | --- | --- |
| `/settings`, `/wallets`, `/verify-identity` | Five shared 11px sidebar group labels per route; `#6f6a62` on `#171310`, 3.44:1 (required 4.5:1) | `frontend/src/components/layout/sidebar.tsx`: remove `/60` from the existing muted foreground token |
| `/operator` | Runtime eyebrow `#c8501e` on `#16120d`, 4.10:1 | `frontend/src/app/operator/(console)/page.tsx`: use existing semantic primary |
| `/operator` | Session-security heading/body/button: 3.13:1, 2.67:1 and 1.24:1 on the translucent warning surface | Same page: opaque pale-red surface with existing scoped light token set and foreground |

Cropped screenshots were visually inspected to confirm the dim group labels and
dark-red warning text on gray. No page-specific wallet/settings/identity logic
was implicated. The fixes preserve permissions, session revocation behavior,
confirmation text and buttons. No tests, lint, typecheck, build or post-fix browser
rerun has been executed for this small class-only batch; the coordinator owns the
collected final sweep and refreshed-image acceptance.

Independent static review additionally found the hidden final confirmation
button would inherit translucent `dark:bg-destructive/20` and hover `/30` despite
the light token scope. On its white inner panel the computed text pairs were
approximately 3.51:1 normal and 3.00:1 hover (the light normal variant was also
below threshold at approximately 4.09:1). The local confirmation button now
explicitly uses opaque red-800 with white text and red-900 with white text on
hover, with matching dark overrides. This is a review-discovered extension of
the same contrast defect, not an executed confirmation/revocation flow or an
additional persona observation. Independent reviewer `recover_session` accepted
the final four-file source diff after this correction, without running tests.
Rebuilt-image verification and the coordinator's final verdict remain pending.
