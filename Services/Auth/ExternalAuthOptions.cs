// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Services.Auth;

/// <summary>
/// Federated sign-in configuration, bound from the <c>Auth</c> section.
/// </summary>
/// <remarks>
/// Client secrets are host-managed and write-only, exactly like the Identomat
/// credentials in <c>appsettings.json</c>: the committed file carries NAMES and blank
/// values, and real values arrive as <c>Auth__Google__ClientSecret</c> /
/// <c>Auth__GitHub__ClientSecret</c> from the deploy secret store. A secret in the
/// repository is a secret in every fork of the repository.
/// </remarks>
public sealed class ExternalAuthOptions
{
    public const string SectionName = "Auth";

    public ExternalAuthProviderOptions Google { get; set; } = new();

    public ExternalAuthProviderOptions GitHub { get; set; } = new();

    /// <summary>
    /// Absolute origin of the browser app that starts the flow, e.g.
    /// <c>https://app.example.com</c>. The callback redirects here and NOWHERE else:
    /// an attacker-supplied absolute URL would turn the callback into an open redirect
    /// that leaks the exchange code to any origin.
    /// </summary>
    public string FrontendBaseUrl { get; set; } = string.Empty;

    /// <summary>Relative path on the frontend that redeems the exchange code.</summary>
    public string FrontendCallbackPath { get; set; } = "/callback";

    /// <summary>
    /// How long a minted exchange code stays redeemable. Deliberately tiny: the code
    /// travels in a URL, and a URL reaches browser history, the Referer header, and
    /// any proxy log on the path.
    /// </summary>
    public int ExchangeCodeTtlSeconds { get; set; } = 120;

    /// <summary>Providers configured well enough to be offered to a user.</summary>
    public IReadOnlyList<string> EnabledProviderNames()
    {
        var enabled = new List<string>(2);
        if (Google.IsUsable) enabled.Add(Models.Auth.ExternalAuthProviderNames.Google);
        if (GitHub.IsUsable) enabled.Add(Models.Auth.ExternalAuthProviderNames.GitHub);
        return enabled;
    }
}

/// <summary>Per-provider client registration.</summary>
public sealed class ExternalAuthProviderOptions
{
    /// <summary>
    /// Operator intent: this node advertises the provider. Enabling WITHOUT supplying
    /// both secrets is a hard startup failure — see <see cref="ExternalAuthRuntimeSafety"/>.
    /// </summary>
    public bool Enabled { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Enabled AND fully credentialed. Both halves are required.</summary>
    public bool IsUsable
        => Enabled
            && !string.IsNullOrWhiteSpace(ClientId)
            && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>Enabled by the operator but missing at least one credential.</summary>
    public bool IsAdvertisedButIncomplete => Enabled && !IsUsable;
}
