-- ============================================================
-- HNSW vector indexes for semantic search
-- Dimension: 384 (MiniLM-style; matches IEmbeddingProvider contract)
-- Distance:  COSINE (dot-product normalised; standard for sentence embeddings)
-- Note: These are additive index definitions only -- no table or field DDL.
--       The embedding fields themselves are defined in 100_holon.surql and
--       150_quest.surql respectively.
-- Note: If the embedding field is absent (NONE) on a row, SurrealDB excludes
--       that row from the index automatically. Queries degrade to linear scan
--       on namespaces where these indexes have not been applied -- see
--       VectorSearchTool warning log.
-- ============================================================

DEFINE INDEX hnsw_holon_embedding
    ON TABLE holon
    FIELDS embedding
    HNSW DIMENSION 384 DIST COSINE;

DEFINE INDEX hnsw_quest_embedding
    ON TABLE quest
    FIELDS embedding
    HNSW DIMENSION 384 DIST COSINE;
