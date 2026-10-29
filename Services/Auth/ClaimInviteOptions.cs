using System.Globalization;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>Node policy for tenant-minted claim invites (<c>Tenant:ClaimInvites</c>); see Services/Auth/AGENTS.md §claim-invites.</summary>
public sealed class ClaimInviteOptions
{
    public const string SectionName = "Tenant:ClaimInvites";

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(72);

    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(7);

    /// <summary>How long a minted invite stays redeemable.</summary>
    public TimeSpan Lifetime { get; init; } = DefaultLifetime;

    /// <summary>Echo the raw token to the minting tenant. Test-harness only: true lets a tenant claim the member's account itself.</summary>
    public bool ReturnTokenToTenant { get; init; }

    /// <summary>Binds and validates the section; throws <see cref="InvalidOperationException"/> naming the bad key.</summary>
    public static ClaimInviteOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        return new ClaimInviteOptions
        {
            Lifetime = ParseLifetime(section[nameof(Lifetime)]),
            ReturnTokenToTenant = ParseFlag(section[nameof(ReturnTokenToTenant)]),
        };
    }

    private static TimeSpan ParseLifetime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultLifetime;
        if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var lifetime))
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(Lifetime)} '{raw}' is not a TimeSpan. Use d.hh:mm:ss, e.g. 3.00:00:00 for 72 hours.");
        if (lifetime <= TimeSpan.Zero || lifetime > MaximumLifetime)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(Lifetime)} '{raw}' parses as {lifetime:c}; it must be positive and at most "
                + $"{MaximumLifetime.TotalDays:0} days. Note that 72:00:00 means 72 DAYS; write 3.00:00:00 for 72 hours.");
        return lifetime;
    }

    private static bool ParseFlag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return bool.TryParse(raw, out var flag)
            ? flag
            : throw new InvalidOperationException($"{SectionName}:{nameof(ReturnTokenToTenant)} '{raw}' must be true or false.");
    }
}
