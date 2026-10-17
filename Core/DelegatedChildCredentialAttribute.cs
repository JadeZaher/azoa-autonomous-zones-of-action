namespace AZOA.WebAPI.Core;

/// <summary>Allowlists an endpoint for tenant-issued child JWTs; see Services/Consent/AGENTS.md §Delegated child credentials.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DelegatedChildCredentialAttribute(string scope, params string[] signingScopes) : Attribute
{
    /// <summary>The literal scope the child credential and the user's live grant must both carry to reach the endpoint.</summary>
    public string Scope { get; } = string.IsNullOrWhiteSpace(scope)
        ? throw new ArgumentException("A delegated child credential policy requires a scope.", nameof(scope))
        : scope;

    /// <summary>Value-signing scopes the endpoint binds internally; delegable so the child can satisfy the signing authority.</summary>
    public IReadOnlyList<string> SigningScopes { get; } = signingScopes;
}
