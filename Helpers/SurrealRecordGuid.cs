using SurrealForge.Client;

namespace AZOA.WebAPI.Helpers;

/// <summary>Normalizes Surreal record identifiers at response-mapping boundaries.</summary>
public static class SurrealRecordGuid
{
    /// <summary>Parses a bare GUID/hex id or a Surreal record link.</summary>
    public static Guid Parse(string value)
    {
        var id = BareId(value);
        return Guid.TryParse(id, out var parsed)
            ? parsed
            : SurrealId.FromSurrealId(id ?? string.Empty);
    }

    /// <summary>Returns a bare record id, including SurrealDB quoted-id normalization.</summary>
    public static string BareId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var id = SurrealLink.FromLink(value) ?? value;
        return id.Length >= 2
               && ((id[0] == '`' && id[^1] == '`') || (id[0] == '"' && id[^1] == '"'))
            ? id[1..^1]
            : id;
    }

    /// <summary>Parses an optional bare id or record link.</summary>
    public static Guid? ParseOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : Parse(value);
}
