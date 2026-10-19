---
type: UX Research Panel
pass: 04-release-readiness
lens: service-designer
status: HYPOTHESES_FOR_VALIDATION
---

# Service designer panel

The observed member and operator navigation handoffs work; the tenant authoring handoff remains incomplete. All three navigation gold tasks passed. The AI author selected planning-only scope and submitted one complete fictional intake, but received clarification questions and no editable graph. This is a partial authoring outcome despite zero scored friction in that run. It does not prove that a further interview turn would fail.

This independent panel read study/scenario/personas, all nine run summaries, the rollup, selected screenshots, the AI lane report and rendered intake/reply evidence, and pass03 public synthesis/verdict. It read no application source, traces, session files or credentials and made no browser, provider or infrastructure calls. Private evidence paths below are locators for authorized review; screenshots, full briefs, transcripts and tenant identifiers are not republished.

## Evidence boundaries and continuity

`ux-report.json` records nine raw runs: eight UI journeys, seven completed and one partial, plus one excluded probe. Coverage is 12/57 page routes (21.1%), with friction 1.5 per UI journey. All three gold tasks passed once each. The AI run's generic `apiCalls=0` conflicts with its lane's separate method/path observation of one actual interview POST; it cannot be read as zero AI use. These navigation gold tasks do not test successful authoring, import, privilege changes or execution.

Compared with `../03-release-readiness/synthesis.md` and `../03-release-readiness/verdict.md`, the single pass04 response now accurately says no draft is available and supplies checklist questions without generated statutory thresholds or rates. The scope control explains manual/external handoff notes and unsupported controls before submission. This is a corrected visible response, not certification of legal/tax accuracy, all model responses or an import path. The settled-animation capture change also limits direct contrast-metric comparisons with pass03.

## Findings

Scores use impact × frequency × segment breadth, each 1–3. Breadth is a provisional service-design estimate, not measured prevalence. All findings remain research hypotheses; no source-level defect is confirmed here.

### SD04-1 — The completeness gate may repeat known facts instead of identifying the missing handoff detail

**Hypothesis:** Broad repeated questions make a prepared author redo the intake and obscure the specific evidence needed to move from interview to editable planning artifact.

**Evidence:** `flows/tenant-workflow-ai/complete-intake-visible.json` supplies exact asset/units, amounts, fee payer, custody, allocation and recordkeeping actors. `flows/tenant-workflow-ai/assistant-reply-settled-visible.txt` asks those categories again. `runs/qa-uxqa-ai-tenant--tenant-economic-workflow-interview/ux.json` records PARTIAL after one submit, no workaround, and no offered review/import control; the lane report records an empty canvas. The settled reply screenshot visually confirms the operational-blocker list and no-draft status.

**Route / Nielsen / JTBD:** `/actions` → Builder → AI interview; Nielsen 6 (recognition), 7 (efficiency), 9 (recovery). Job: turn specified economic and responsibility decisions into an editable planning artifact with explicit unresolved work.

**Important distinction:** Questions about the evidence approvers must review and who performs external settlement can legitimately seek finer specificity. The brief describes approval/dispute actors and reconciliation states but does not fully specify every evidence source or external settlement performer. Their presence alone is not a defect. The apparent duplicates concern the already-populated asset/custody/allocation/recordkeeping categories. The one-request study budget, rather than observed abandonment or a failed second turn, ended this attempt.

**Rating:** impact 2, frequency 1, breadth 2 = **4 / LOW** under the supplied rubric. Confidence: **high** that category repetition and missing artifact occurred; **medium** that repetition caused the withheld draft; **low** on recurrence. The observed authoring goal was incomplete, but costly repeat input is the demonstrated issue; irrecoverable goal failure is unproved.

**Human confirm / kill:** Run five prepared tenant-author sessions using complete briefs, with permission and budget for normal follow-up. Confirm if authors cannot identify the missing detail or must restate accepted values to progress. Kill if each broad question is understood as a clear request for genuinely new information, existing facts stay recognized, and the next appropriate answer consistently yields a reviewable artifact. Separately inspect the completeness decision during triage before assigning a code fix.

### SD04-2 — Honest planning scope leaves a separate operational authoring job unresolved

**Hypothesis:** People seeking a working approval-and-payment process may still need substantial help handing a planning artifact to the people and systems that must implement its controls.

**Evidence:** The before-submit scope and settled reply in `flows/tenant-workflow-ai/before-single-submit-visible.txt` and `assistant-reply-settled-visible.txt` consistently describe manual/external notes, unsupported approval/cap/refund/payment enforcement, and later explicit webhook effects. They state that checkpoints neither wait for nor prove approval or payment. The same lane report confirms that no graph was offered, so the quality of any generated handoff package is unobserved.

**Route / Nielsen / JTBD:** `/actions`; Nielsen 2 (real-world match), 10 (help/documentation). Job: move from a business process brief to assigned implementation, review, settlement and evidence responsibilities.

**Classification:** This is an **operational capability and service-handoff research question**, not evidence of a false promise. The interface explicitly limits the assistant to planning, and distinguishes import from execution. Removing those limits would not resolve the gap. Whether the offered planning product satisfies the intended authoring job must be tested separately from whether the copy is truthful.

**Rating:** impact 2, frequency 1, breadth 2 = **4 / LOW**. Confidence: **high** about the disclosed capability boundary; **low** about user expectation or downstream handoff failure.

**Human confirm / kill:** With five business-process authors and five receiving implementer/reviewer participants, ask what deliverable they expect before submission and what they would do next with a planning-only artifact. Confirm if the recipient cannot identify responsible actors, required controls, evidence and implementation work without recreating the brief. Kill if authors intentionally want planning notes and recipients can use them to make an accurate implementation/review plan. A future safe import observation is needed before evaluating the actual artifact.

### SD04-3 — An interrupted interview may lose the only usable bridge to another responsible person

**Hypothesis:** When no graph is available, browser-only interview retention may force an interrupted author to reconstruct decisions and unresolved questions before handing work to an adviser or implementer.

**Evidence:** `flows/tenant-workflow-ai/before-single-submit-visible.txt` states that only the latest eight turns remain in the browser view and closing the page or changing account discards the interview. The final rendered reply and `runs/qa-uxqa-ai-tenant--tenant-economic-workflow-interview/ux.json` establish a partial interview with no importable graph. Closing/re-entry, export and collaboration were not exercised; loss and recovery behavior are not verified.

**Route / Nielsen / JTBD:** `/actions`; Nielsen 1 (status), 3 (control), 7 (efficiency). Job: pause and resume planning, or transfer an accurate brief and open decisions to another responsible person.

**Rating:** impact 2, frequency 1, breadth 2 = **4 / LOW**. Confidence: **high** about the displayed retention warning; **low** about actual interruption costs or missing recovery alternatives.

**Human confirm / kill:** Interrupt five tenant-author sessions after clarification, then ask participants to resume and give a recipient a usable decision record. Confirm if reconstruction loses accepted facts, open questions or role ownership. Kill if an available understood recovery/export method preserves the necessary context, or if participants' intended short interview use does not require continuity. Test with synthetic data only.

## Authority and recovery observations

The mobile route `/login` offers a visible operator-sign-in handoff; anonymous `/operator/audit` redirects to `/operator/login`, and the recovery gold completes in three seconds. Its 401 plus console event account for measured friction but are not by themselves a failed service handoff. `runs/qa-uxqa-mobile--mobile-comprehension-corrected-replay/operator-login.png` names deployment-configured credentials and a secret-store recovery boundary. Actual credential recovery, contact escalation and post-sign-in return to the original audit destination remain untested; no failure is asserted.

`runs/qa-uxqa-operator--operator-readiness/node-overview.png` distinguishes ending this browser session from revoking all operator sessions. `providers.png` distinguishes policy edits from host secrets and says recovery refresh cannot approve an identity. `tenants.png` connects selectable tenant providers to node-enabled, ready profiles. These are useful authority boundaries across operator/deployer/tenant roles. No mutation, expired authenticated session, provider outage recovery or cross-role completion was exercised, so read-only completion does not validate those services. Keyboard and zoom runs reached the not-started identity state; their console 404 does not establish a visible verification failure.

Zero human sessions occurred. Agent patience, explicit task goals and a single prepared author can hide misunderstanding or inflate apparent success. No release approval, legal/compliance determination, operational automation, successful graph import or human prevalence claim follows from this panel.
