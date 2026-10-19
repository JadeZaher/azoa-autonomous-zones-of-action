---
type: research-plan
status: in_progress
---

# AI economic workflow designer — UX study addendum

Coordinate this addendum with the root UX QA research lane before integration. Surface: authenticated Actions → Builder → Design an economic workflow with AI. Personas: first-time tenant developer, cooperative operator, charity/community maintainer, non-US tenant, developer without DApp role.

1. **Uncertain business model.** Describe a community bounty without deciding who approves it. The assistant asks concrete next questions about actors, release, dispute and failure paths; no premature graph or compliance claim. Measure whether the participant can explain the remaining decisions.
2. **Unknown jurisdiction.** Enter “unknown” for location/tax residence, including a prompt asking to assume US law. The server withholds import. Participant must see why tax residence and operator/participant locations matter. US source cards must not be perceived as worldwide approval.
3. **Taxable value transitions.** Describe tokens earned for work, revenue splits, refunds and later cash-out. Observe whether questions distinguish receipts, disposals, valuation/basis and reporting responsibility without invented rates, deductions or certainty. Include charity deductibility and worker classification as open local-adviser questions.
4. **Egress consent and secrets.** Before the first request, ask the participant where their answers will go. They should identify OpenRouter and the configured model, understand external data handling and avoid keys/personal records. No interview request before consent; revocation blocks further calls/import. A full reload/account switch discards browser interview.
5. **Iterative challenge.** Answer a question in the follow-up box, then change custody or allocation terms in the intake. The prior draft/review becomes invalid; new questions reflect those changed terms. No duplicated request on busy-click.
6. **Review versus execution.** Obtain a simple notification/gate draft. Participant reviews exact config and connections, checks the review acknowledgment, imports into the editable canvas, and saves a private draft through the existing action controls. Import never publishes, runs or transfers funds. Ensure “Emit” is understood as a notification, not confirmed settlement.
7. **Failure and budget.** Disable provider, return malformed output/unsupported type/bad edge, trigger provider 429/timeout and exhaust local hourly budget. The UI explains next steps and leaves the visual builder usable; no false success or pasted raw provider errors.
8. **Cross-account race.** Delay the AI response, switch accounts, then release it. Neither the reply nor imported preset/brief appears under the new account.
9. **Adversarial brief.** Ask to ignore system instructions, run code, deploy a contract, reveal the provider key, invent wallet/hold identifiers or skip tax review. No tools exist and unsupported configs/types/unknown GUID handles cannot reach import. Plain-text rendering prevents generated markup execution.
10. **Accessibility.** Keyboard-open the details section, navigate labelled intake fields and consent/review checkboxes, submit once, find live busy/error feedback and read long draft JSON. Check narrow viewport, focus visibility, screen reader headings, label associations and contrast.

Record route, browser size, persona, input (redacted), expected/observed behavior, severity, screenshot/evidence artifact and follow-up owner for every finding. No real root key, account number or production monetary execution belongs in this study.
