namespace AZOA.WebAPI.Helpers;

/// <summary>Preserves the existing saga opaque-key storage format; see Services/Sagas/AGENTS.md.</summary>
public static class SagaOpaqueKey
{
    private const string Tag = "s_";
    private const string Colon = "_C_";

    public static string Encode(string value) => string.IsNullOrEmpty(value) ? value : Tag + value.Replace(":", Colon);

    public static string Decode(string value)
        => value.StartsWith(Tag, StringComparison.Ordinal) ? value[Tag.Length..].Replace(Colon, ":") : value;
}
