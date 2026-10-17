using System.Text.Json;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Helpers;

/// <summary>Canonical identity, cycle prevention and rolling evidence commitment for bounded provider reads.</summary>
public static class WttEvidenceProgressContract
{
    public static WttEvidenceProgress Begin(string bindingJson, string stateJson)
    {
        RequireCanonical(bindingJson); RequireCanonical(stateJson);
        var id = CanonicalJson.Hash(new { Version = 1, Domain = "azoa:wtt:evidence", Binding = bindingJson });
        return new() { Id = id, BindingJson = bindingJson, InitialStateJson = stateJson, StateJson = stateJson,
            Digest = CanonicalJson.Hash(new { Version = 1, Binding = bindingJson, InitialState = stateJson }),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
    }

    public static WttEvidenceProgress Advance(WttEvidenceProgress expected, string stateJson, string pageDigest, string? nextCursor)
    {
        RequireCanonical(stateJson);
        if (expected.Complete || expected.Version < 0 || expected.Pages != expected.Version || pageDigest is not { Length: 64 }
            || !pageDigest.All(char.IsAsciiHexDigitLower) || nextCursor is { Length: 0 } || nextCursor is { Length: > 8192 })
            throw new InvalidDataException("Invalid WTT evidence page transition.");
        var cursors = JsonSerializer.Deserialize<List<string>>(expected.SeenCursorHashesJson)
            ?? throw new InvalidDataException("WTT evidence cursor history is missing.");
        if (cursors.Count != expected.Pages || cursors.Distinct(StringComparer.Ordinal).Count() != cursors.Count
            || cursors.Any(value => value is not { Length: 64 } || !value.All(char.IsAsciiHexDigitLower))
            || expected.Pages > 0 && (expected.NextCursor is null || cursors[^1] != CanonicalJson.Hash(expected.NextCursor)))
            throw new InvalidDataException("WTT evidence cursor lineage is inconsistent.");
        if (nextCursor is not null)
        {
            var hash = CanonicalJson.Hash(nextCursor);
            if (cursors.Contains(hash, StringComparer.Ordinal)) throw new InvalidDataException("WTT evidence query returned a repeated cursor.");
            cursors.Add(hash);
        }
        return new() { Id = expected.Id, BindingJson = expected.BindingJson, InitialStateJson = expected.InitialStateJson,
            StateJson = stateJson, NextCursor = nextCursor, SeenCursorHashesJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(cursors)),
            Digest = CanonicalJson.Hash(new { Version = 1, Previous = expected.Digest, Page = pageDigest, State = stateJson, Next = nextCursor }),
            Pages = checked(expected.Pages + 1), Version = checked(expected.Version + 1), Complete = nextCursor is null,
            CreatedAt = expected.CreatedAt, UpdatedAt = DateTimeOffset.UtcNow };
    }

    private static void RequireCanonical(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || CanonicalJson.Canonicalize(document.RootElement) != json)
            throw new InvalidDataException("WTT evidence requires a canonical object.");
    }
}
