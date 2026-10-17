namespace AZOA.WebAPI.Core;

/// <summary>Allowlists an endpoint for tenant-issued child JWTs carrying <see cref="Scope"/>; see Services/Consent/AGENTS.md §Delegated child credentials.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DelegatedChildCredentialAttribute(string scope) : Attribute
{
    /// <summary>The literal scope the child credential and the user's live grant must both carry.</summary>
    public string Scope { get; } = string.IsNullOrWhiteSpace(scope)
        ? throw new ArgumentException("A delegated child credential policy requires a scope.", nameof(scope))
        : scope;
}
