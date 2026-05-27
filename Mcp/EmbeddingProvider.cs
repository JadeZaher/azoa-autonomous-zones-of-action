namespace OASIS.WebAPI.Mcp;

/// <summary>
/// Abstraction over an embedding model.
/// Returns a 384-dimensional vector for any input text.
/// Caller must NOT cache across instances — the provider is the cache boundary.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>
    /// Returns a 384-dimensional embedding vector for the given text.
    /// Caller must NOT cache across instances — the provider is the cache boundary.
    /// </summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}

/// <summary>
/// Placeholder provider that produces deterministic 384-dimensional vectors
/// from a SHA-256 of the input text.
///
/// <para>
/// Purpose: lets the MCP <c>vector_search</c> tool surface and the HNSW index
/// ship NOW without an external embedding service dependency. Swap in a real
/// embedder (OpenAI <c>text-embedding-3-small</c>, local Ollama
/// <c>nomic-embed-text</c>, etc.) as a one-class replacement when
/// productionizing — no call-site changes required.
/// </para>
///
/// <para>
/// <b>DO NOT use in production.</b> Semantic search quality is zero:
/// the SHA-256-derived vectors carry no linguistic meaning. Any two strings
/// that differ by a single character will produce entirely unrelated vectors.
/// Production code MUST register a real embedder before enabling
/// <c>vector_search</c>.
/// </para>
/// </summary>
public sealed class DeterministicDummyEmbeddingProvider : IEmbeddingProvider
{
    /// <inheritdoc/>
    public Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text ?? ""));

        // Expand 32-byte hash into 384 floats deterministically: tile the hash
        // 12 times and map each byte to a normalised float in [-1, 1].
        var vec = new float[384];
        for (int i = 0; i < 384; i++)
            vec[i] = (bytes[i % 32] / 127.5f) - 1.0f;

        return Task.FromResult(vec);
    }
}
