-- ============================================================
-- Table: hnsw_quest_embedding
-- Note: HNSW vector index definitions for holon and quest tables.
-- Note: DIMENSION 384 -- MiniLM-style sentence embedding size; matches IEmbeddingProvider.EmbedAsync return length.
-- Note: DIST COSINE -- cosine similarity is the standard distance metric for normalised sentence embeddings. Euclidean (DIST EUCLIDEAN) would require L2-normalised vectors and is not equivalent for dot-product models.
-- Note: No entity body here -- these are pure index declarations. Table and field DDL lives in 100_holon.surql / 150_quest.surql. This file mirrors the documentation-only pattern of 230_quest_graph_edges.mermaid.
-- Note: Rows with embedding IS NONE are excluded from the HNSW index automatically. VectorSearchTool's WHERE embedding IS NOT NONE predicate keeps the query plan honest on fresh namespaces without indexes.
-- ============================================================

DEFINE TABLE IF NOT EXISTS hnsw_quest_embedding SCHEMAFULL;

-- HNSW index on quest.embedding (DIMENSION 384, DIST COSINE)
DEFINE FIELD IF NOT EXISTS table ON TABLE hnsw_quest_embedding TYPE string;
DEFINE FIELD IF NOT EXISTS fields ON TABLE hnsw_quest_embedding TYPE string;
