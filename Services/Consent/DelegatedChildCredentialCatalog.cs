using System.Reflection;
using AZOA.WebAPI.Core;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Services.Consent;

/// <summary>The delegable child-credential scope vocabulary: exactly the scopes some allowlisted endpoint accepts.</summary>
public static class DelegatedChildCredentialCatalog
{
    private static readonly Lazy<IReadOnlySet<string>> AllowlistedScopes = new(() => Endpoints()
        .Select(endpoint => endpoint.Policy.Scope).ToHashSet(StringComparer.Ordinal));

    /// <summary>Scopes a tenant may delegate onto a child credential.</summary>
    public static IReadOnlySet<string> Scopes => AllowlistedScopes.Value;

    /// <summary>Every controller action carrying a <see cref="DelegatedChildCredentialAttribute"/>.</summary>
    public static IEnumerable<(Type Controller, MethodInfo Action, DelegatedChildCredentialAttribute Policy)> Endpoints()
        => typeof(DelegatedChildCredentialCatalog).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(method => (Controller: type, Action: method, Policy: method.GetCustomAttribute<DelegatedChildCredentialAttribute>()))
                .Where(entry => entry.Policy is not null)
                .Select(entry => (entry.Controller, entry.Action, entry.Policy!)));
}
