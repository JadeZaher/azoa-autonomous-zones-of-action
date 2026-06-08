// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the hnsw_holon_embedding virtual/index-only table.

#nullable enable

using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("hnsw_holon_embedding")]
    [SurrealNote("HNSW vector index definitions for holon and quest tables.")]
    [SurrealNote("DIMENSION 384 -- MiniLM-style sentence embedding size; matches IEmbeddingProvider.EmbedAsync return length.")]
    [SurrealNote("DIST COSINE -- cosine similarity is the standard distance metric for normalised sentence embeddings. Euclidean (DIST EUCLIDEAN) would require L2-normalised vectors and is not equivalent for dot-product models.")]
    [SurrealNote("No entity body here -- these are pure index declarations. Table and field DDL lives in 100_holon.surql / 150_quest.surql. This file mirrors the documentation-only pattern of 230_quest_graph_edges.mermaid.")]
    [SurrealNote("Rows with embedding IS NONE are excluded from the HNSW index automatically. VectorSearchTool's WHERE embedding IS NOT NONE predicate keeps the query plan honest on fresh namespaces without indexes.")]
    [Slice("_skip")]
    [VirtualTable]
    public partial class HnswHolonEmbedding : ISurrealRecord
    {
        public const string SchemaNameConst = "hnsw_holon_embedding";
        public string SchemaName => SchemaNameConst;

        [Column(Order = 1, Type = "string")]
        [FieldGroup("HNSW index on holon.embedding (DIMENSION 384, DIST COSINE)")]
        [JsonPropertyName("table")]
        public string Table { get; set; } = string.Empty;

        [Column(Order = 2, Type = "string")]
        [JsonPropertyName("fields")]
        public string Fields { get; set; } = string.Empty;
    }
}
