using System.Text.RegularExpressions;

namespace AZOA.WebAPI.Services.Admin;

/// <summary>Canonical identity and credential-validation rules for the node operator.</summary>
public static partial class NodeOperatorIdentity
{
    public static readonly Guid AvatarId = Guid.Parse("a20a0000-0000-4000-8000-000000000001");
    public const string ReservedEmail = "node-operator@azoa.invalid";

    public static string NormalizeUsername(string? value)
        => value?.Trim().ToLowerInvariant() ?? string.Empty;

    public static bool IsValidUsername(string? value)
        => UsernamePattern().IsMatch(NormalizeUsername(value));

    public static bool IsStructurallyValidPasswordHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var match = PasswordHashPattern().Match(value);
        return match.Success
            && int.TryParse(match.Groups["cost"].Value, out var cost)
            && cost is >= 12 and <= 31;
    }

    public static bool VerifyPassword(string password, string? passwordHash)
    {
        if (!IsStructurallyValidPasswordHash(passwordHash))
            return false;
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string? Validate(NodeOperatorOptions options)
    {
        var username = NormalizeUsername(options.Username);
        if (!IsValidUsername(username))
            return "NodeOperator:Username must be 3-64 lowercase letters, digits, dots, underscores, or hyphens.";

        var password = options.Password ?? string.Empty;
        if (password.Length < 24
            || System.Text.Encoding.UTF8.GetByteCount(password) > 72
            || password.Any(char.IsWhiteSpace)
            || password.Any(char.IsControl)
            || IsKnownPlaceholder(password))
        {
            return "NodeOperator:Password must be a 24-72 byte generated secret without whitespace or placeholder text.";
        }

        if (string.Equals(username, password, StringComparison.OrdinalIgnoreCase))
            return "NodeOperator:Password must not equal the username.";
        if (options.CredentialRevision < 1)
            return "NodeOperator:CredentialRevision must be a positive monotonic integer.";
        if (options.SessionMinutes is < 5 or > 30)
            return "NodeOperator:SessionMinutes must be between 5 and 30.";

        return null;
    }

    private static bool IsKnownPlaceholder(string value)
        => value.Contains("change-me", StringComparison.OrdinalIgnoreCase)
            || value.Contains("replace", StringComparison.OrdinalIgnoreCase)
            || value.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
            || value.All(character => character == value[0]);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,62}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    [GeneratedRegex(@"^\$2[aby]\$(?<cost>\d{2})\$[./A-Za-z0-9]{53}$", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordHashPattern();
}
