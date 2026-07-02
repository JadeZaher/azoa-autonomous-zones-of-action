using System.Text.Json;
using System.Text.Json.Serialization;
using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Services.Quest;

/// <summary>
/// Shared safe-deserialization helper for quest node configs.
/// All handlers MUST use TryDeserialize instead of Deserialize&lt;T&gt;(...)!
/// See Services/Quest/AGENTS.md §node-config.
/// </summary>
public static class QuestNodeConfig
{
    /// <summary>
    /// Strict options: unknown members are disallowed so typos fail loudly at
    /// definition time. Case-insensitive property matching preserved for
    /// backward compat with existing stored configs.
    /// </summary>
    public static readonly JsonSerializerOptions StrictOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Attempts to deserialize <paramref name="json"/> as <typeparamref name="T"/>.
    /// Returns true on success; on failure sets <paramref name="error"/> to a
    /// message carrying the node type name and parse detail.
    /// Null/empty <paramref name="json"/> returns a default-constructed instance
    /// (not an error) — handlers that need a non-null value get the default.
    /// </summary>
    public static bool TryDeserialize<T>(
        string? json,
        string nodeTypeName,
        out T config,
        out string error) where T : new()
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            config = new T();
            return true;
        }

        try
        {
            var result = JsonSerializer.Deserialize<T>(json, StrictOptions);
            if (result is null)
            {
                config = new T();
                error  = $"[{nodeTypeName}] config deserialized to null.";
                return false;
            }

            config = result;
            return true;
        }
        catch (JsonException ex)
        {
            config = new T();
            error  = $"[{nodeTypeName}] config parse error: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Validates that <paramref name="json"/> can be strictly deserialized as
    /// <typeparamref name="T"/>. Returns null on success, or an error string.
    /// Used at definition time (AddNode/UpdateNode/publish gate).
    /// </summary>
    public static string? ValidateStrict<T>(string? json, string nodeTypeName) where T : new()
    {
        TryDeserialize<T>(json, nodeTypeName, out _, out var error);
        return string.IsNullOrEmpty(error) ? null : error;
    }
}
