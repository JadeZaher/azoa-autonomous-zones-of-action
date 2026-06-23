using System.Text.Json;

namespace AZOA.WebAPI.Core.Json;

/// <summary>
/// Helpers for converting between caller-facing
/// <see cref="Dictionary{TKey, TValue}"/> shapes and the
/// <see cref="JsonElement"/> shape that the source-gen'd SurrealDB POCOs use
/// for SurrealDB <c>object</c> fields (e.g. <c>dapp_series.shared_config</c>,
/// <c>quest.metadata</c>).
/// </summary>
/// <remarks>
/// The source generator emits <see cref="JsonElement"/> for SurrealDB
/// <c>object</c> fields because it cannot know the runtime shape of an
/// open-ended object. Callers prefer the <see cref="Dictionary{TKey, TValue}"/>
/// shape; this helper bridges them without leaking the JsonElement to API
/// surfaces.
/// </remarks>
public static class JsonElementExtensions
{
    /// <summary>
    /// Materialize a string-to-string map from a <see cref="JsonElement"/>
    /// whose <see cref="JsonElement.ValueKind"/> is
    /// <see cref="JsonValueKind.Object"/>. Non-object kinds return an empty
    /// dictionary; non-string values are converted via
    /// <see cref="JsonElement.ToString"/>.
    /// </summary>
    public static Dictionary<string, string> ToStringDictionary(this JsonElement element)
    {
        var dict = new Dictionary<string, string>();
        if (element.ValueKind != JsonValueKind.Object)
            return dict;

        foreach (var property in element.EnumerateObject())
        {
            dict[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Null => string.Empty,
                _ => property.Value.ToString(),
            };
        }
        return dict;
    }

    /// <summary>
    /// Round-trip a <see cref="Dictionary{TKey, TValue}"/> into a
    /// <see cref="JsonElement"/> by serializing and re-parsing. The
    /// allocation cost is acceptable for these small config blobs (typically
    /// under a dozen keys) and keeps the storage shape consistent with what
    /// the SurrealDB persistence path produces.
    /// </summary>
    public static JsonElement ToJsonElement(this IReadOnlyDictionary<string, string> dictionary)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(dictionary);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Convenience for the common case where the caller has a nullable
    /// dictionary and wants an empty <see cref="JsonElement"/> object when
    /// the input is null.
    /// </summary>
    public static JsonElement ToJsonElementOrEmpty(this IReadOnlyDictionary<string, string>? dictionary)
    {
        if (dictionary is null)
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
        return dictionary.ToJsonElement();
    }
}
