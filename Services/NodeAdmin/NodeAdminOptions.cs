namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Environment configuration for node admins (<c>NodeAdmin__*</c>); see <c>Services/NodeAdmin/AGENTS.md</c> §configuration.</summary>
public sealed class NodeAdminOptions
{
    public const string SectionName = "NodeAdmin";

    /// <summary>Comma-separated base64url raw Ed25519 public keys allowed to log in as break-glass root.</summary>
    public string? RootPublicKeys { get; set; }

    /// <summary>Monotonic root key revision; lowering it disables root login, bumping it ends every root session.</summary>
    public long RootKeyRevision { get; set; }

    /// <summary>The node's canonical public origin; defaults to <c>PUBLIC_ORIGIN</c>.</summary>
    public string? Audience { get; set; }

    /// <summary>Session lifetime in minutes, clamped to 5..30.</summary>
    public int SessionMinutes { get; set; } = 15;

    /// <summary>Enrollment token lifetime in minutes, clamped to 10..240.</summary>
    public int EnrollmentMinutes { get; set; } = 240;

    /// <summary>Node-wide cap on live (unconsumed, unexpired) login challenges; reaching it refuses new challenges (M-3).</summary>
    public int MaxOutstandingChallenges { get; set; } = 10_000;

    public TimeSpan SessionLifetime => TimeSpan.FromMinutes(Math.Clamp(SessionMinutes, 5, 30));

    public TimeSpan EnrollmentLifetime => TimeSpan.FromMinutes(Math.Clamp(EnrollmentMinutes, 10, 240));
}
