namespace AZOA.WebAPI.Helpers;

/// <summary>
/// Chain-name normalization helpers.
/// </summary>
public static class ChainNames
{
    public static string NormalizeChain(string? chainType)
        => (chainType ?? string.Empty).Trim().ToLowerInvariant();
}
