---
type: UX Synthesis
pass: 01-release-readiness
status: PENDING_COORDINATOR_RERUN
releaseVerdict: NOT_READY
---

# Initial release synthesis

Pass 01 did not meet study exit criteria. Its frozen report records nine runs:
eight UI runs and one separate probe. UI outcomes are six completed, one partial
and one abandoned; two of three gold tasks passed. Coverage is 11/57 pages
(19.3%), below the unchanged 20% target. Reported UI friction is 74/8 = 9.25.
These counts include disclosed automation problems and cannot be read as a human
abandonment or failure rate. No human participants were studied.

This synthesis merges the independent [accessibility](panel-accessibility.md),
[content](panel-content-designer.md), [service](panel-service-designer.md), and
[trust/behavioural](panel-trust-behavioural.md) lenses. Each lens read the lane
evidence independently without application source. Code-confirmed triage is a
subsequent step, recorded in [ledger.json](ledger.json) and [fix-batch.md](fix-batch.md).
The source fixes and pass-02 follow-up do not retrospectively turn pass 01 green.

## Affinity map and ranked interpretation

| Merged theme | Panel IDs | Evidence and contradiction | Triage |
| --- | --- | --- | --- |
| Perceive the next step | A11Y-01/02/03, CD-02, SD-02/03 | Empty menu name, repeated contrast detections, Overview focusability warning. Pointer zoom navigation and desktop keyboard destinations nevertheless completed | Menu and contrast MEDIUM; scroll LOW provisional under original panel scoring. Confirmed source fixes; follow-up required |
| Bound unavailable information | CD-01, SD-01, TB-03 | Two technical chain exceptions on Overview. Both members continued, so a total-service misunderstanding is predicted rather than observed | MEDIUM. Keep chain identity and honest unavailable copy; actual balance/provider health is unproved |
| Distinguish default policy from old policy | CD-04, SD-04, TB-04 | Four provider cards showed the epoch date; disabled/configuration labels supplied useful counterevidence | LOW. Source confirms version-zero built-ins, not four independent stale-policy events |
| Choose the right authority and handoff | CD-03/05, SD-05/06, TB-01/02/05 | Role-specific login and browser/global-session copy existed; Settings mixed personal and tenant tools. No wrong edit or actual credential-loss recovery occurred | Existing LOW/MEDIUM research questions; no speculative navigation/permission redesign |
| Understand disclosure before proceeding | TB-06 | Identity not-started state and provider wording were read, but no disclosure/consent flow was entered | MEDIUM research question, not a demonstrated privacy breach |

The highest supported ranked findings are shared text contrast (panel score 12,
MEDIUM) and the missing menu name (score 9 / accessibility exception, MEDIUM).
Raw chain wording and settings/disclosure comprehension received provisional
MEDIUM scores around 8; only the wording has a confirmed code defect. Provider
freshness, recovery ownership and navigation effort remain LOW where scored.
There is no panel-supported new HIGH/BLOCKER in this small sample; final new-high
gate evaluation and adversarial approval remain pending, rather than inferred
from that absence. Axe “critical/serious” labels are not ledger severity scores.

## Friction and evidence quality

The home-route heatmap is highest at 32. Its abandonment arose before a click
because a strict locator matched two LOGIN elements. The separately recorded
corrected mobile journey completed with two clicks and no workaround. That peak
does not demonstrate product-induced abandonment or jargon incomprehension.

Overview's heatmap value 12 combines raw chain-status friction and accessibility
signals while users sought account status. Operator audit's 10 includes signed-out
recovery/request activity and visits; it does not establish that an authenticated
audit failed. Personal verification's 7 supports reading/status investigation,
not failure of a verification submission that was never attempted. Dead/rage
clicks and recorded workarounds were zero. Rule-route scan occurrences are not
unique defects or affected people.

The settings gold run is PARTIAL even though its recorded attempt succeeded;
navigation/predicate timing was unresolved in this pass. The original mobile
failure and this result remain in the raw aggregate. The operator lane disclosed
an initial no-TTY blocked run overwritten at the same task path before its
successful UI restart, limiting run-history completeness. Comparison with
`00-setup` is invalid as a usability trend because that was a login smoke with
different tasks and coverage.

## Heuristics and jobs

| Nielsen relationship | Route/job | Supported conclusion |
| --- | --- | --- |
| H4 consistency, H6 recognition | Find account destinations through collapsed navigation | Programmatic menu name was absent; human exclusion remains a hypothesis |
| H1 status, H9 recovery | Decide what chain information is unavailable and what can continue | Technical wording was unhelpful; do not infer balance loss or node outage |
| H1/H2 status and real-world match | Distinguish an untouched provider from an old saved policy | Epoch display misrepresented an unset state; consequences require human validation |
| H2/H5/H6 scope and prevention | Pick a personal-account setting or consent decision | Mixed scope and expected recipients need comprehension research; no unsafe choice observed |
| H7 efficiency | Reach repeated destinations and scroll content by keyboard | 52 navigation keys and one focusability warning justify investigation, not an invented keyboard trap |

Gold tasks establish reaching ordinary settings, an operator sign-in explanation
and an audit view. They do not prove credential recovery, populated audit
correctness or authorization enforcement. Route/API inventories also do not cover
every .NET endpoint.

## Validation and next pass

Run the [human validation plan](../../human-validation.md): five sessions per
affected segment, with falsifiers and constrained assistive-technology tasks.
Every open research question is linked in the ledger. Tenant AI consent,
jurisdiction/legal/tax warnings and draft-only comprehension were outside this
pass and need their own future scenario; model probes do not substitute for it.

Agents are unusually patient, literate and goal-directed, and replay familiarity
can hide discovery costs. The zoom harness halves CSS viewport size at double
density; it is not a real browser zoom-control or assistive-technology assessment.
Empty-state read-only journeys do not establish consequential-write safety or full
WCAG conformance. Preserve the initial evidence and use separately named refreshed
image runs. [Pass 02](../02-release-readiness/synthesis.md) is follow-up evidence,
not a replacement for this failed pass or a completed human study.
