---
type: futurist
track_id: commercial-primitives-identomat-access-links_20261115
---

# Futurist Roadmap: Next-Cycle Evolution

1. **Identomat Live Webhook & Liveness Replay Harness**:
   - Once Identomat credentials arrive, wire signed Identomat callback verification into `CommercialPrimitivesController` with deterministic replay fixtures matching `commercial-primitives.v1.json`.
2. **MoonPay / FastWallet Instant Checkout & Webhook Receipt Linking**:
   - Auto-issue `OwnershipReceiptDocument` entries upon confirmed settlement webhooks so investors receive cryptographic SHA-256 receipts immediately after on-ramp/swap completion.
3. **PDF & Signed Verifiable Credential Export for Account Documents**:
   - Extend `AccountDocumentReport.downloadUrl` from JSON/print summaries to server-rendered signed PDFs and VC bundles anchored to content hashes.
4. **SDK Registry Publish (`azoa-sdk`)**:
   - After final commit on `main`, publish updated `azoa-sdk` version and switch ArdaNova from local vendored copy back to registry tarball verification.
