// SPDX-License-Identifier: UNLICENSED

using Microsoft.AspNetCore.Authentication;
using AZOA.WebAPI.Models.Auth;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>
/// Fail-fast boot guard for federated sign-in (Phase 1B, D18).
/// </summary>
/// <remarks>
/// <para>Mirrors <c>BlockchainProviderFactory.GuardAgainstUnregisteredEnabledChains</c>
/// and <c>KycRuntimeSafety.GuardProductionProviderIsReal</c>: the operator's ADVERTISED
/// intent is diffed against what is actually usable, and a gap stops the host rather
/// than degrading at request time.</para>
///
/// <para>Without this, <c>Auth:Google:Enabled=true</c> with an absent
/// <c>Auth__Google__ClientSecret</c> boots healthy, renders a Google button, and 500s
/// the first user who presses it — a defect discovered by a user, in production, on the
/// authentication path. Reading configuration directly (rather than asking the
/// registered handler) is the same reasoning the KYC guard documents: the handler
/// registration silently tolerates blank credentials, so asking it would hide the exact
/// defect this catches.</para>
/// </remarks>
public static class ExternalAuthRuntimeSafety
{
    /// <summary>
    /// Rejects a provider advertised as enabled without a complete client registration.
    /// </summary>
    /// <remarks>
    /// Enforced in EVERY environment, not just Production. A developer whose local
    /// secrets are missing should see the failure at boot with a message naming the
    /// variable, not a broken redirect ten minutes later.
    /// </remarks>
    public static void GuardEnabledProvidersAreCredentialed(ExternalAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var incomplete = new List<string>(2);
        if (options.Google.IsAdvertisedButIncomplete)
            incomplete.Add(Describe(ExternalAuthProviderNames.Google, "Google", options.Google));
        if (options.GitHub.IsAdvertisedButIncomplete)
            incomplete.Add(Describe(ExternalAuthProviderNames.GitHub, "GitHub", options.GitHub));

        if (incomplete.Count == 0)
            return;

        throw new InvalidOperationException(
            "Federated sign-in is advertised as enabled for a provider whose client "
            + "registration is incomplete, which would present a sign-in button that "
            + "fails at redirect time. Supply the missing secrets from the host secret "
            + "store, or set the provider's Auth:<Provider>:Enabled to false. "
            + string.Join(" ", incomplete));
    }

    /// <summary>
    /// Rejects a Production host whose federated callback has no absolute frontend
    /// origin to return to.
    /// </summary>
    /// <remarks>
    /// The callback refuses to honour a caller-supplied absolute URL (that would be an
    /// open redirect leaking the exchange code), so a blank origin in Production means
    /// every federated sign-in dead-ends after the user has already authorised the app.
    /// </remarks>
    public static void GuardProductionRedirectOrigin(
        IHostEnvironment environment,
        ExternalAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);

        if (!environment.IsProduction())
            return;
        if (options.EnabledProviderNames().Count == 0)
            return;

        if (!Uri.TryCreate(options.FrontendBaseUrl, UriKind.Absolute, out var origin)
            || (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                "Auth:FrontendBaseUrl must be an absolute http(s) origin when a federated "
                + "sign-in provider is enabled in Production. Set it via the "
                + "Auth__FrontendBaseUrl environment variable; the OAuth callback will not "
                + "redirect to a caller-supplied address.");
        }

        if (origin.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Auth:FrontendBaseUrl must use https in Production: the OAuth callback "
                + "redirect carries a single-use exchange code in its query string.");
        }
    }

    /// <summary>
    /// Proves at BOOT that everything federated sign-in needs actually resolves from
    /// the container, and that each advertised provider has a registered
    /// authentication scheme.
    /// </summary>
    /// <remarks>
    /// <para>Configuration guards above catch a missing SECRET. This catches a missing
    /// REGISTRATION — a different failure with the same symptom for the user. A service
    /// that is never registered builds fine and passes a unit suite (tests construct the
    /// type directly), then throws on the first real request; the same class of defect
    /// as an unregistered <c>IBridgeProtocol</c>. Resolving once at startup converts
    /// that into a boot failure naming the type.</para>
    ///
    /// <para>Scheme presence is checked too, because the handler registrations are
    /// conditional on <see cref="ExternalAuthProviderOptions.IsUsable"/>: if that
    /// condition and <see cref="ExternalAuthOptions.EnabledProviderNames"/> ever
    /// disagree, the node would advertise a button whose challenge has no handler.</para>
    /// </remarks>
    public static void GuardRegistrationsResolve(
        IServiceProvider services,
        ExternalAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var advertised = options.EnabledProviderNames();
        if (advertised.Count == 0)
            return;

        using var scope = services.CreateScope();

        foreach (var required in new[]
                 {
                     typeof(IExternalLoginService),
                     typeof(IOAuthExchangeCodeService),
                     typeof(IUserLoginTokenIssuer),
                 })
        {
            if (scope.ServiceProvider.GetService(required) is null)
            {
                throw new InvalidOperationException(
                    $"Federated sign-in is enabled but {required.Name} is not registered. "
                    + "The host would build and then fail on the first sign-in request. "
                    + "Register it in Program.cs alongside the other Services/Auth types.");
            }
        }

        var schemes = scope.ServiceProvider
            .GetRequiredService<IAuthenticationSchemeProvider>();

        foreach (var providerName in advertised)
        {
            if (schemes.GetSchemeAsync(providerName).GetAwaiter().GetResult() is null)
            {
                throw new InvalidOperationException(
                    $"Federated sign-in advertises \"{providerName}\" but no authentication "
                    + "scheme of that name is registered, so its sign-in button would fail at "
                    + "challenge time. The scheme registration and the advertised provider "
                    + "list have diverged.");
            }
        }
    }

    /// <summary>Binds the <c>Auth</c> section and runs every guard.</summary>
    public static ExternalAuthOptions GuardStartup(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ExternalAuthOptions();
        configuration.GetSection(ExternalAuthOptions.SectionName).Bind(options);

        GuardEnabledProvidersAreCredentialed(options);
        GuardProductionRedirectOrigin(environment, options);
        return options;
    }

    private static string Describe(
        string configKey,
        string displayName,
        ExternalAuthProviderOptions provider)
    {
        var missing = new List<string>(2);
        if (string.IsNullOrWhiteSpace(provider.ClientId))
            missing.Add($"Auth__{ConfigSegment(configKey)}__ClientId");
        if (string.IsNullOrWhiteSpace(provider.ClientSecret))
            missing.Add($"Auth__{ConfigSegment(configKey)}__ClientSecret");

        return $"{displayName} is missing: {string.Join(", ", missing)}.";
    }

    private static string ConfigSegment(string providerRouteSegment)
        => providerRouteSegment == ExternalAuthProviderNames.GitHub ? "GitHub" : "Google";
}
