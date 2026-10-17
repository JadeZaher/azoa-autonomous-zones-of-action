-- Defensive: round-3 lane C replaced the unique index ethereum_preparation_nonce with the generated
-- ethereum_preparation_live_nonce (protocol_identity, sender, nonce, nonce_slot); a stale index left on any
-- database would keep a second, differently-shaped uniqueness rule. No-op where it never existed.
-- See ../AGENTS.md §migrations.
REMOVE INDEX IF EXISTS ethereum_preparation_nonce ON TABLE ethereum_preparation;
