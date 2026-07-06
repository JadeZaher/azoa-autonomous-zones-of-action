namespace AZOA.WebAPI.Services.Admin;

/// <summary>
/// Config for the operator:admin bootstrap seam (H2). See
/// <see href="../../docs/NODE-HOST.md">NODE-HOST.md §8.9</see> for the operator
/// procedure and Services/Admin/AGENTS.md for the fail-closed rationale.
/// Bind from <see cref="SectionName"/>:
/// <code>
/// builder.Services
///     .AddOptions&lt;AdminBootstrapOptions&gt;()
///     .Bind(builder.Configuration.GetSection(AdminBootstrapOptions.SectionName));
/// </code>
/// </summary>
public sealed class AdminBootstrapOptions
{
    /// <summary>Configuration section name: <c>"AdminBootstrap"</c>.</summary>
    public const string SectionName = "AdminBootstrap";

    /// <summary>
    /// The email of the avatar to promote to <c>operator:admin</c> on JWT mint.
    /// Empty/unset means bootstrap is OFF — no avatar is ever stamped.
    /// </summary>
    public string? SeedEmail { get; set; }

    /// <summary>
    /// Shared secret an operator must present (env-driven) to prove intent to
    /// bootstrap. Empty/unset means bootstrap is OFF regardless of
    /// <see cref="SeedEmail"/> — this is the fail-closed gate.
    /// </summary>
    public string? SeedSecret { get; set; }
}
