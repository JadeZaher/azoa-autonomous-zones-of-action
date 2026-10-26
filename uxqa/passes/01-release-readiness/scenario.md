---
type: UX Pass Scenario
title: Node administration release readiness
description: Six local lanes assess authorization understanding, operator recovery and everyday dashboard navigation.
---

# Situation

A developer is returning to an AZOA node after an administration release. They need to find account settings and understand where operator authority belongs. An operator reviews node readiness and the history of identity-policy changes. An administrator evaluates the CLI's available commands and denied/disabled behavior before the owner performs a separate live root-key drill.

Use only the disposable local E2E stack with marked accounts. This pass is research-only (`fix: false`): record failures, synthesize evidence, and hand confirmed defects to the release coordinator's existing fix batch. Do not perform value movement, root-key operations, role changes, provider changes, outbound notifications or deployment work. UI journeys cannot inspect source or call the API. The probe lane can do so but is excluded from UI success metrics.

Gold goals: understand sign-in recovery on an anonymous operator visit; find one's account/integration settings; find identity-policy audit history. Other journeys test mobile visible-text comprehension, keyboard navigation, 200% zoom and slow network. Each run records a trace, screenshots, axe findings, attempts, abandonment and workarounds. A failed journey is evidence, not a reason to substitute an API shortcut.

For every observation, record the goal, expected behavior, actual behavior, evidence path and whether the person recovered unaided. Research questions stay hypotheses and receive a five-sessions-per-segment human validation plan. CLI capability checks are not usability studies of human command-line behavior.

Route scope is the public/sign-in shell, ordinary overview/settings/wallet/identity screens, and read-only operator overview/audit/tenant/provider screens. Target route coverage is 20% of the 57-page inventory; report exact numerator and denominator and leave unvisited routes explicit. A route-loading sweep cannot by itself establish usable task completion.

No prior study exists. There is no friction trend baseline and no claim of two consecutive passing passes. No prebake fixtures beyond marked accounts are needed; record an empty manifest after confirming that choice. If the operator fixture no longer authenticates due to the new RBAC model, record it as an OWNER/setup blocker and preserve the failure; do not invent or grant authority.
