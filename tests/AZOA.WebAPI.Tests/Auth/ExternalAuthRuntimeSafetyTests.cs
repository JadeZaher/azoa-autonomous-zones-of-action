// SPDX-License-Identifier: UNLICENSED
//
// Phase 1B boot guard — a provider advertised without credentials must stop the host,
// mirroring KycRuntimeSafety.GuardProductionProviderIsReal.

using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using AZOA.WebAPI.Services.Auth;

namespace AZOA.WebAPI.Tests.Auth;

public class ExternalAuthRuntimeSafetyTests
{
    /// <summary>
    /// The headline case: Production, Google advertised, secret absent. Booting would
    /// render a Google button that 500s on the first user who pressed it — a defect
    /// found by a user, on the authentication path, in production.
    /// </summary>
    [Fact]
    public void A_provider_enabled_without_a_client_secret_fails_startup_in_production()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Auth:Google:Enabled"] = "true",
            ["Auth:Google:ClientId"] = "google-client-id.apps.googleusercontent.com",
            // Auth__Google__ClientSecret deliberately absent, as on a host whose
            // secret store was never populated.
            ["Auth:FrontendBaseUrl"] = "https://app.example.com",
        });

        var act = () => ExternalAuthRuntimeSafety.GuardStartup(Environment("Production"), configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Auth__Google__ClientSecret*");
    }

    /// <summary>The same gap stops a DEVELOPMENT host too — fail at boot, not at redirect.</summary>
    [Fact]
    public void A_provider_enabled_without_a_client_secret_also_fails_startup_in_development()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Auth:GitHub:Enabled"] = "true",
            ["Auth:GitHub:ClientId"] = "Iv1.abc123",
        });

        var act = () => ExternalAuthRuntimeSafety.GuardStartup(Environment("Development"), configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Auth__GitHub__ClientSecret*");
    }

    /// <summary>A missing client ID is named just as precisely as a missing secret.</summary>
    [Fact]
    public void A_provider_enabled_without_a_client_id_fails_startup()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Auth:Google:Enabled"] = "true",
            ["Auth:Google:ClientSecret"] = "secret-value",
            ["Auth:FrontendBaseUrl"] = "https://app.example.com",
        });

        var act = () => ExternalAuthRuntimeSafety.GuardStartup(Environment("Production"), configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Auth__Google__ClientId*");
    }

    /// <summary>
    /// A provider that is simply switched off is not a misconfiguration. This is the
    /// committed appsettings.json default and it must boot cleanly.
    /// </summary>
    [Fact]
    public void A_disabled_provider_with_no_secrets_starts_normally()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Auth:Google:Enabled"] = "false",
            ["Auth:GitHub:Enabled"] = "false",
        });

        var options = ExternalAuthRuntimeSafety.GuardStartup(Environment("Production"), configuration);

        options.EnabledProviderNames().Should().BeEmpty();
    }

    /// <summary>A fully credentialed provider boots and is advertised to the frontend.</summary>
    [Fact]
    public void A_fully_credentialed_provider_starts_and_is_advertised()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Auth:Google:Enabled"] = "true",
            ["Auth:Google:ClientId"] = "id",
            ["Auth:Google:ClientSecret"] = "secret",
            ["Auth:FrontendBaseUrl"] = "https://app.example.com",
        });

        var options = ExternalAuthRuntimeSafety.GuardStartup(Environment("Production"), configuration);

        options.EnabledProviderNames().Should().BeEquivalentTo(["google"]);
    }

    /// <summary>
    /// Production with an enabled provider but no absolute frontend origin dead-ends
    /// every sign-in AFTER the user has already authorised the app, because the
    /// callback refuses caller-supplied absolute URLs by design.
    /// </summary>
    [Fact]
    public void Production_requires_an_absolute_frontend_origin_when_a_provider_is_enabled()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Auth:Google:Enabled"] = "true",
            ["Auth:Google:ClientId"] = "id",
            ["Auth:Google:ClientSecret"] = "secret",
            ["Auth:FrontendBaseUrl"] = "",
        });

        var act = () => ExternalAuthRuntimeSafety.GuardStartup(Environment("Production"), configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Auth:FrontendBaseUrl*");
    }

    /// <summary>The exchange code rides in a query string, so Production demands https.</summary>
    [Fact]
    public void Production_refuses_a_plaintext_http_frontend_origin()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["Auth:Google:Enabled"] = "true",
            ["Auth:Google:ClientId"] = "id",
            ["Auth:Google:ClientSecret"] = "secret",
            ["Auth:FrontendBaseUrl"] = "http://app.example.com",
        });

        var act = () => ExternalAuthRuntimeSafety.GuardStartup(Environment("Production"), configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("*https*");
    }

    /// <summary>The committed appsettings.json must itself boot a Production host.</summary>
    [Fact]
    public void The_committed_configuration_boots_cleanly()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var act = () => ExternalAuthRuntimeSafety.GuardStartup(Environment("Production"), configuration);

        act.Should().NotThrow(
            "the committed file ships both providers disabled with blank secret NAMES only");
    }

    // ── Registration guard: a missing DI registration must fail at BOOT ───────

    /// <summary>
    /// The failure mode a green unit suite cannot see: the service is never registered,
    /// the host builds, and the first sign-in throws. Resolving once at startup turns
    /// that into a boot failure that names the type.
    /// </summary>
    [Fact]
    public void An_enabled_provider_whose_services_are_unregistered_fails_startup()
    {
        var options = UsableGoogleOptions();
        var services = new ServiceCollection();
        services.AddAuthentication().AddCookie("google");

        using var provider = services.BuildServiceProvider();

        var act = () => ExternalAuthRuntimeSafety.GuardRegistrationsResolve(provider, options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IExternalLoginService*not registered*");
    }

    /// <summary>
    /// A provider advertised to the frontend with no authentication scheme behind it
    /// would render a button whose challenge has nothing to handle it.
    /// </summary>
    [Fact]
    public void An_advertised_provider_with_no_authentication_scheme_fails_startup()
    {
        var options = UsableGoogleOptions();
        var services = RegisteredAuthServices();
        // Deliberately register a scheme under the WRONG name.
        services.AddAuthentication().AddCookie("not-google");

        using var provider = services.BuildServiceProvider();

        var act = () => ExternalAuthRuntimeSafety.GuardRegistrationsResolve(provider, options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*google*scheme*");
    }

    /// <summary>A fully wired node passes the registration guard.</summary>
    [Fact]
    public void A_fully_registered_provider_passes_the_registration_guard()
    {
        var options = UsableGoogleOptions();
        var services = RegisteredAuthServices();
        services.AddAuthentication().AddCookie("google");

        using var provider = services.BuildServiceProvider();

        var act = () => ExternalAuthRuntimeSafety.GuardRegistrationsResolve(provider, options);

        act.Should().NotThrow();
    }

    /// <summary>A node offering no providers needs none of it and must not fail.</summary>
    [Fact]
    public void A_node_with_no_providers_skips_the_registration_guard()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();

        var act = () => ExternalAuthRuntimeSafety.GuardRegistrationsResolve(
            provider, new ExternalAuthOptions());

        act.Should().NotThrow();
    }

    private static ExternalAuthOptions UsableGoogleOptions() => new()
    {
        Google = new ExternalAuthProviderOptions
        {
            Enabled = true,
            ClientId = "id",
            ClientSecret = "secret",
        },
    };

    /// <summary>The Services/Auth registrations Program.cs makes, minus the schemes.</summary>
    private static ServiceCollection RegisteredAuthServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IExternalLoginService>());
        services.AddSingleton(Mock.Of<IOAuthExchangeCodeService>());
        services.AddSingleton(Mock.Of<IAvatarLoginTokenIssuer>());
        return services;
    }

    private static IConfiguration Build(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IHostEnvironment Environment(string environmentName)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(host => host.EnvironmentName).Returns(environmentName);
        return environment.Object;
    }
}
