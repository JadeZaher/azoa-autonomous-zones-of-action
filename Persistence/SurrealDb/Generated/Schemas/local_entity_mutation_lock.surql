-- ============================================================
-- Table: local_entity_mutation_lock
-- Aggregate: Local graph mutation serialization
-- Guardrail: Server-owned table locks
-- ============================================================

DEFINE TABLE IF NOT EXISTS local_entity_mutation_lock SCHEMAFULL;

DEFINE FIELD IF NOT EXISTS id ON TABLE local_entity_mutation_lock TYPE string;
DEFINE FIELD IF NOT EXISTS token ON TABLE local_entity_mutation_lock TYPE string;
