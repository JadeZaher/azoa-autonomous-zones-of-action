---
type: UX Research Panel
title: Pass 04 independent content-design panel
description: Source-free content hypotheses from the recorded release-readiness journeys.
---

# Content-design panel

The recorded interface explains its planning limits more accurately than a completed workflow outcome would imply. The tenant interview nevertheless stopped before a draft: some follow-up questions restated information already supplied, while other questions could reasonably seek finer operational detail. The main content opportunity is to identify the missing detail precisely, then test whether people understand the planning contract without having to retain a long disclaimer.

This panel independently read `study.md`, this pass's scenario/personas/report, all nine run JSON files, the private tenant lane report, rendered intake and response evidence, and selected current screenshots covering public entry, sign-in recovery, member status, wallets, operator readiness/audit/providers and the AI reply. It did not inspect application source, executable flows, traces, credentials or infrastructure, and made no browser, network or model requests. Paths below identify private evidence for authorized review; no screenshots, raw brief, transcript or tenant identifiers are reproduced here.

## Recorded observations

- `ux-report.json` records eight UI journeys: seven `COMPLETED`, one tenant interview `PARTIAL`; the ninth run is a separate probe. All three gold predicates passed. Coverage is 12/57 pages (21.05%), with no unmatched paths, and UI friction is 1.5 per journey. These are constrained agent results, not human comprehension scores or whole-product release approval.
- The tenant run records one submitted interview and no offered graph/import control; the visible canvas remained empty. Its friction score is zero despite its incomplete objective. Neither zero friction nor zero recorded axe findings measures whether the questions were useful or the content was understood.
- The rendered response says a draft is unavailable and separates operational planning blockers from adviser-review questions. It does not claim that a draft exists. The supplied intake already specifies value units, amounts, custody/control, allocation, fee responsibility and recordkeeping; two returned questions ask those categories again. The approval-evidence and settlement-handoff questions can seek detail beyond those broad categories. A second exchange was outside the one-request allowance, so eventual recovery is unknown.
- The scope choice, response and capability notes explicitly distinguish a planning graph from money movement and business-control enforcement. Import is described as non-executing; a later explicit run can enqueue configured webhook notifications. Checkpoints are described as neither waiting for nor proving approval/payment. No graph was returned, so the behavior of an imported artifact was not observed.
- The legal/tax text presents an issue checklist and adviser questions rather than a classification, threshold, rate or determination. Reading guides are labeled US examples and starting points rather than support for a conclusion. Their visible subjects remain digital assets, virtual currency and crypto assets for this fiat-only scenario. This is a content-scope observation, not a legal/tax accuracy or compliance assessment.
- Mobile entry successfully followed `LOGIN`, then `Open operator sign in`. The recovery destination identifies operator credentials and their deployment owner. Keyboard and zoom journeys found wallets, settings and identity status. The identity screen explains that verification has not started and distinguishes `Start verification` from the disabled `Refresh result`. No verification or recovery action was initiated.
- Current operator screenshots show readiness information, configuration boundaries and a distinction between ending the current browser session and revoking all sessions. The overview displays ready checklist items in this fixture. A lane step's general conclusion that setup is needed should therefore not replace the actual displayed state.

## Hypotheses for validation

Scores use impact × frequency × segment breadth, each 1–3. Frequency describes observed journeys, not the number of repeated questions. Breadth is kept to the observed affected segment. All findings below are research questions; none establishes a source-level defect or authorizes a code fix.

### CD04-1 — Broad follow-ups can hide the exact missing information

**Hypothesis:** When a complete-looking brief receives generic questions about already-entered value and allocation facts, a workflow designer may not know what to change. The participant could repeat a long answer instead of supplying the narrower evidence or settlement responsibility that would move planning forward.

**Evidence:** `flows/tenant-workflow-ai/complete-intake-visible.json` compared directly with `flows/tenant-workflow-ai/assistant-reply-settled-visible.txt`; `runs/qa-uxqa-ai-tenant--tenant-economic-workflow-interview/ux.json` and `assistant-reply-settled.png`. Route: `/actions`. The input and output were compared directly; the lane summary is supporting interpretation. Two questions restate supplied categories; two seek potentially narrower detail. No second-turn failure is established.

**Nielsen / JTBD:** 6 recognition rather than recall; 9 diagnose and recover. Job: turn my operational brief into a reviewable planning draft and understand exactly what still prevents it.

**Rating:** impact 3 (recorded planning goal incomplete), frequency 1, breadth 1 = **3, LOW by the study rubric**. High confidence in the repeated categories and missing draft; moderate confidence in the human recovery problem. The lane report's MEDIUM candidate is not adopted as measured severity: one journey cannot establish repeated path frequency or wider reach, and this is not a gold task or demonstrated accessibility exclusion.

**Human confirmation / kill:** In five sessions with tenant workflow designers, give each a complete fictional intake and ask them to highlight the genuinely missing facts and describe their next edit before continuing. Confirm if participants cannot distinguish restated facts from requested additions or repeatedly paste unchanged material. Kill if all five identify a specific unanswered operational detail, make a targeted edit and progress without material repetition. Review the decision rule separately during source-authorized triage; do not infer its implementation from this reply.

### CD04-2 — A truthful scope choice may still be too dense to remember

**Hypothesis:** The planning option's long radio label combines money limits, business-control limits, import behavior, future notifications and checkpoint semantics. People may select it while retaining only “planning graph,” then overestimate what approvals or records mean later.

**Evidence:** `flows/tenant-workflow-ai/assistant-reply-settled-visible.txt`; `runs/qa-uxqa-ai-tenant--tenant-economic-workflow-interview/assistant-reply-settled.png` and `ux.json`. Route: `/actions`. The limits are visibly present and the recorded agent deliberately selected them; misunderstanding was not observed. Repetition in the response offers useful reinforcement.

**Nielsen / JTBD:** 2 match the real world; 6 recognition rather than recall; 8 minimalist design. Job: choose planning knowingly and explain what import, checkpoints and a later run will do.

**Rating:** impact 2 (potential costly misunderstanding), frequency 1, breadth 1 = **2, LOW**. Moderate confidence in reading burden; low confidence in actual misunderstanding. Do not remove material limits merely to shorten the label.

**Human confirmation / kill:** Five novice tenant designers, including readers who prefer simple language, must teach back whether funds move, approvals are enforced, imports execute, and a later run may notify configured recipients. Confirm if users give an incorrect answer after choosing scope or cannot find the relevant limit. Kill if all five explain those boundaries accurately without moderator explanation. Compare a short choice label plus adjacent readable scope details only after observing the current copy.

### CD04-3 — US example links may feel unrelated to a fiat workflow

**Hypothesis:** Clearly scoped but exclusively crypto-oriented reading guides may leave a fiat-workflow designer unsure where to begin relevant adviser preparation. The risk is an unhelpful research detour rather than a demonstrated false legal claim.

**Evidence:** `flows/tenant-workflow-ai/complete-intake-visible.json` and `assistant-reply-settled-visible.txt`; tenant `ux.json`. Route: `/actions`. The workflow is fiat-only; guide labels concern digital assets, virtual currency business models and crypto assets. The surrounding copy correctly limits their geographic and evidentiary scope. Links were not followed or verified by this panel.

**Nielsen / JTBD:** 2 match the real world; 10 help/documentation. Job: prepare relevant questions and identify appropriate local professional review without mistaking examples for tailored coverage.

**Rating:** impact 1, frequency 1, breadth 1 = **1, LOW**. High confidence in topic mismatch; low-to-moderate confidence in its practical effect.

**Human confirmation / kill:** In five sessions with fiat-workflow designers, ask which resources address their scenario, which jurisdictions they cover and what remains for an adviser. Confirm if participants waste time on irrelevant guides or assume they cover the actual local worker/payment questions. Kill if all five recognize the examples' limits and can identify a suitable next review step without confusion. A qualified reviewer would assess substantive resource relevance separately; this panel makes no treatment or compliance determination.

### CD04-4 — Public automation promises may exceed the AI planning mental model

**Hypothesis:** A newcomer who reads the public site's financial-workflow, approval and refund examples may expect the AI assistant to automate those outcomes. The later planning disclaimers are explicit, but discovering that narrower scope only inside the builder could feel like a change in promise.

**Evidence:** `runs/qa-uxqa-mobile--mobile-comprehension-corrected-replay/public-home.png` and `ux.json`, contrasted with the tenant settled visible reply. Routes: `/` → `/actions`. The public page describes a wider product; the assistant describes its own narrower capability. These can both be accurate. The mobile agent did not perform the tenant journey, so a cross-touchpoint misunderstanding has not been observed.

**Nielsen / JTBD:** 2 match the real world; 4 consistency. Job: decide what AZOA and its AI builder can deliver before investing effort in a detailed brief.

**Rating:** impact 2, frequency 1, breadth 1 = **2, LOW**. Moderate confidence in differing scope emphasis; low confidence in an experienced mismatch.

**Human confirmation / kill:** Five first-time prospective workflow designers should read the public page, predict what the AI builder will produce, then inspect scope before entering a fictional brief. Confirm if they expect automatic payouts/control enforcement and fail to update that model after the scope choice. Kill if all five distinguish overall product capabilities from this assistant's planning output and make an informed choice without wasted input.

### CD04-5 — Honest unavailable-recovery text may leave novices without a next contact

**Hypothesis:** A signed-out novice may understand that account recovery is unavailable or operator credentials belong to the deployment owner, yet still not know whom to contact. “Node,” “deployer” and “secret store” are meaningful to operators but may not help a low-literacy member who entered the wrong sign-in path.

**Evidence:** `runs/qa-uxqa-mobile--mobile-comprehension-corrected-replay/member-login.png`, `signed-out-audit-recovery.png` and `ux.json`; `runs/qa-uxqa-mobile--gold-operator-recovery/ux.json`. Routes: `/login`, `/operator/login`. Ordinary sign-in has a visible operator link, and the operator destination has an ordinary-sign-in return link. Both recorded navigation tasks succeeded; lost-password recovery and contact discovery were not tested.

**Nielsen / JTBD:** 2 match the real world; 9 recovery; 10 help. Job: recover access or find the correct responsible person without guessing at technical administration.

**Rating:** impact 2, frequency 1, breadth 1 = **2, LOW**. High confidence that the limitations are stated; low confidence in an actual recovery barrier.

**Human confirmation / kill:** Five newcomers who prefer plain language should explain which sign-in they need and where they would seek help with forgotten credentials, without actually resetting anything. Confirm if they choose the wrong owner or cannot identify a next step. Kill if all five choose the correct path and responsible person unaided. Preserve the honest unavailability statement; do not imply that a reset service exists.

## Interpretation limits

The strongest finding is the directly evidenced mismatch between supplied categories and repeated questions. Its source and eventual recoverability remain unknown. Zero agent abandonment, rapid task times and zero automated accessibility findings do not establish that low-literacy people can understand the introduction, legal checklist, scope label or recovery instructions. The AI lane was neither mobile nor a low-literacy participant. No locale was exercised, and encoding artifacts in shell-rendered text are not treated as proof of visible localization defects.

The report's friction improvement from 1.75 in pass03 to 1.5 in pass04 accompanies a disclosed settled-animation capture method change. It is not an isolated content effect. Current evidence supports truthful no-draft status, clear capability boundaries in the available text, and successful navigation predicates. It supports no claim of human validation, legal/tax compliance, functional graph import, economic automation or study completion. Human sessions above are a proposed plan, not completed research.
