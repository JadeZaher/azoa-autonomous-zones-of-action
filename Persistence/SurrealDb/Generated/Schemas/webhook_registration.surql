-- ============================================================
-- Table: webhook_registration
-- Aggregate: WebhookRegistration (Models/WebhookRegistration.cs)
-- Guardrail: G6 SCHEMAFULL, one active registration per tenant (unique tenant_id)
-- Note: A tenant's outbound consent-webhook config (tenant-consent-delegation §4, AC7): receiver url + per-tenant HMAC secret. ONE active registration per tenant for v1, enforced by the unique tenant_id index. Registration is scoped to the authenticated API-key principal — a tenant only ever reads/writes its OWN row (H5).
-- Note: url is https-only + public-IP-allowlisted, re-validated by WebhookSsrfGuard at DELIVERY time (not just registration) so a DNS rebind to a private address between register and deliver is still caught. secret is the per-tenant HMAC key (rotatable); each tenant's events are signed with ONLY its own secret — no shared secret.
-- Note: REQUIRED before real use: secret MUST be encrypted-at-rest (AES-256-GCM via WalletKeyService, mirroring Wallet.EncryptedPrivateKey) — the not-yet-built registration write-path encrypts before persisting and the delivery worker decrypts at read time. It is still persisted as-supplied today only because no write path exists yet. The API-facing model's Secret is [JsonIgnore]'d so it never leaves in an HTTP response; this storage column intentionally is not, since it must serialize to/from the DB.
-- ============================================================

DEFINE TABLE IF NOT EXISTS webhook_registration SCHEMAFULL;

-- Core identity
DEFINE FIELD IF NOT EXISTS id ON TABLE webhook_registration TYPE string
    ASSERT $value != NONE AND $value != "";

-- Owning tenant — unique, one active registration per tenant for v1
DEFINE FIELD IF NOT EXISTS tenant_id ON TABLE webhook_registration TYPE record<avatar>;

-- Receiver endpoint — https-only + public-IP, SSRF-guarded at delivery time
DEFINE FIELD IF NOT EXISTS url ON TABLE webhook_registration TYPE string
    ASSERT $value != NONE AND $value != "";

-- Per-tenant HMAC secret (rotatable). REQUIRED: encrypt-at-rest like Wallet.EncryptedPrivateKey before real use.
DEFINE FIELD IF NOT EXISTS secret ON TABLE webhook_registration TYPE string
    ASSERT $value != NONE AND $value != "";

-- When the secret was last rotated (NONE if never rotated since creation)
DEFINE FIELD IF NOT EXISTS secret_rotated_at ON TABLE webhook_registration TYPE option<datetime>;

-- Whether deliveries are active — inactive causes the worker to skip/dead-letter the tenant's events
DEFINE FIELD IF NOT EXISTS is_active ON TABLE webhook_registration TYPE bool
    DEFAULT true;

-- Timestamps
DEFINE FIELD IF NOT EXISTS created_at ON TABLE webhook_registration TYPE datetime
    READONLY;

-- ── Indexes ──────────────────────────────────────────────────

DEFINE INDEX IF NOT EXISTS webhook_registration_tenant_unique
    ON TABLE webhook_registration
    FIELDS tenant_id
    UNIQUE;
