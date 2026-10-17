---
type: review
---

# Ethereum NFT contract source review

Reviewer: root, separate from contract author. Initial bounded pass; source is still being completed and no execution evidence exists.

Two findings require correction before source acceptance:

1. Acceptance was only a consumed stage key. Delivery did not require prior recipient acceptance bound to the same lifecycle, token and recipient. Record acceptance identity and enforce it at delivery, including retries after proven no effect. Cover skipped acceptance, recipient substitution, token substitution and lifecycle mismatch.
2. A burned token passed the new-mint owner-is-zero check, allowing immutable issuer/soulbound provenance to be overwritten by a later mint. Retain an ever-minted identity guard and test burn/remint rejection.

Pinning common lifecycle identity across intermediary stages also prevents unrelated stage permits from being combined. All effect-bearing entrypoints must retain the platform policy permit and the relevant owner/issuer/recipient permit. This review does not claim the pending provider adapter or nonce recovery implementation is approved.
