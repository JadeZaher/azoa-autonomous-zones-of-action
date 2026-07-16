namespace AZOA.WebAPI.Core.Surreal;

/// <summary>Encodes scalar strings safely for raw SurrealQL expressions.</summary>
public static class SurrealScalarString
{
    /// <summary>Splits a scalar into safely bound characters for <c>array::join</c>.</summary>
    public static string[] ToCharacters(string? value)
        => value?.Select(character => character.ToString()).ToArray() ?? [];
}
