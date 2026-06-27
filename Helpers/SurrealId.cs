namespace AZOA.WebAPI.Helpers;

/// <summary>
/// Canonical conversions between a <see cref="Guid"/> and its SurrealDB
/// record-id rendering (32-char lowercase hex, no dashes). Promoted from the
/// identical private forks that previously lived in every Surreal store and
/// saga store, so the whole repo shares ONE implementation.
/// </summary>
public static class SurrealId
{
    public static string ToSurrealId(Guid id) => id.ToString("N").ToLowerInvariant();

    public static Guid FromSurrealId(string id) => Guid.ParseExact(id, "N");
}
