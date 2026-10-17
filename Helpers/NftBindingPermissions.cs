namespace AZOA.WebAPI.Helpers;

/// <summary>Resolves explicit binding grants and exact named levels, with explicit deny taking precedence.</summary>
public static class NftBindingPermissions
{
    public static bool Grants(string? level, IReadOnlyDictionary<string, string> permissions, string permission)
    {
        if (permissions.TryGetValue(permission, out var value)) return bool.TryParse(value, out var enabled) && enabled;
        return string.Equals(level, "full", StringComparison.OrdinalIgnoreCase)
            || (permission == "read" && string.Equals(level, "read-only", StringComparison.OrdinalIgnoreCase))
            || (permission is "read" or "write" or "admin" && string.Equals(level, permission, StringComparison.OrdinalIgnoreCase));
    }
}
