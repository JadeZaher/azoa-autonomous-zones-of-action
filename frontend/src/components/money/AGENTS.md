# First-party money SDK adoption

Funds and cash-out consume `azoa-sdk/money` through its first-party transport and render
`azoa-sdk/react` with local design slots. Only the member's own first-party session is used;
no operator or tenant credential enters the provider. The generic SDK sale transport is absent
because AZOA has no host-specific project-unit pricing contract.

Before mounting either form, migrate legacy unresolved keys to the subject-scoped SDK store.
Preserve the exact key, amount and known server id. A conflicting or unreadable legacy record
blocks the form; choosing one would allow a duplicate payment. Remove the old storage entry only
after the SDK copy is readable. The existing USD checkout denomination follows the node's current
fiat configuration; changing that configuration also requires changing the explicitly displayed
currency. Tenant payout-float remains its separate operator-facing workflow.

The direct API client captures the member's token rather than sharing the mutable global
client. KYC continuation also checks the live session still matches before sending.
Storage migration validates both copies using the SDK strict parser and preserves conflicting server ids.

The surface accepts a redirect callback for embedding and browser fixtures; its default
still navigates to the SDK-validated URL. Alert slots use a block container because SDK
notices contain paragraphs and controls.
