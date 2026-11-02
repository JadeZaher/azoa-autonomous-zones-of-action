namespace AZOA.WebAPI.Services.Auth;

/// <summary>Opt-in member delivery configuration; see AGENTS.md §member-handoff.</summary>
public sealed class MemberHandoffOptions
{
    public bool Enabled { get; init; }
    public bool StubDeliveryAcknowledged { get; init; }
    public string HostedBaseUrl { get; init; } = string.Empty;
    public string[] AllowedReturnOrigins { get; init; } = [];

    public static MemberHandoffOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Tenant:ClaimInvites");
        return new MemberHandoffOptions
        {
            Enabled = section.GetValue<bool>(nameof(Enabled)),
            StubDeliveryAcknowledged = section.GetValue<bool>(nameof(StubDeliveryAcknowledged)),
            HostedBaseUrl = section[nameof(HostedBaseUrl)] ?? string.Empty,
            AllowedReturnOrigins = section.GetSection(nameof(AllowedReturnOrigins)).Get<string[]>() ?? [],
        };
    }

    public void Validate(bool local)
    {
        if (!Enabled) return;
        if (!StubDeliveryAcknowledged && !local)
            throw new InvalidOperationException("Enabled member handoffs use an operator delivery stub; Tenant:ClaimInvites:StubDeliveryAcknowledged must be explicitly true.");
        if (!SafeOrigin(HostedBaseUrl, local) || AllowedReturnOrigins.Length == 0
            || AllowedReturnOrigins.Any(origin => !SafeOrigin(origin, local)))
            throw new InvalidOperationException("Member handoffs require a safe HostedBaseUrl origin and explicit AllowedReturnOrigins.");
    }

    public bool AllowsReturn(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/portfolio/withdraw"
            && string.IsNullOrEmpty(uri.Query)
            && AllowedReturnOrigins.Any(origin => Uri.TryCreate(origin, UriKind.Absolute, out var allowed)
                && string.Equals(allowed.GetLeftPart(UriPartial.Authority), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));

    private static bool SafeOrigin(string value, bool local)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/"
            && (uri.Scheme == "https" || (local && uri.Scheme == "http" && uri.IsLoopback));
}
