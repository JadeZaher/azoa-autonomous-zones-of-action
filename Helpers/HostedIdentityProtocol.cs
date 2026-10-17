using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Providers.Kyc;

namespace AZOA.WebAPI.Helpers;

/// <summary>Strict identity protocol parsing and authenticated transport primitives.</summary>
public static class HostedIdentityProtocol
{
    public static JsonDocument Parse(string body)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(body) > 65536) throw new JsonException("Identity payload is too large.");
        var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 24 });
        try { CheckDuplicates(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    public static string? Text(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    public static bool Fields(JsonElement value, params string[] allowed)
        => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().All(property => allowed.Contains(property.Name, StringComparer.Ordinal));

    public static bool SafeToken(string? value, int max = 256)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);

    public static bool Origin(string? value, out string origin)
    {
        origin = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || uri.IsLoopback || IdentomatKycProviderService.IsInternalHost(uri)) return false;
        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    public static bool Redirect(string? value, IReadOnlyList<string> allowedOrigins)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) && !uri.IsLoopback
            && !IdentomatKycProviderService.IsInternalHost(uri)
            && allowedOrigins.Any(allowed => Origin(allowed, out var origin) && origin == uri.GetLeftPart(UriPartial.Authority));

    public static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    public static bool UtcInstant(string? text, out DateTimeOffset instant)
    {
        instant = default;
        return text is not null && text.Contains('T') && (text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal))
            && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out instant)
            && instant.Offset == TimeSpan.Zero;
    }

    public static string Hmac(string secret, string value)
        => Convert.ToHexStringLower(HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(value)));

    public static bool VerifyHmac(string? signature, string secret, string value)
    {
        if (signature is not { Length: 64 } || !signature.All(Uri.IsHexDigit)) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature), Convert.FromHexString(Hmac(secret, value)));
    }

    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate identity protocol field.");
                CheckDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
    }
}
