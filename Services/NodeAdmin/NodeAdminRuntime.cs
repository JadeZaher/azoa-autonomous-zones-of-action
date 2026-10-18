using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Validated node-admin configuration: audience, root keys and the boot-time root-login decision (M5).</summary>
public sealed class NodeAdminRuntime
{
    private readonly List<RootKey> _rootKeys = [];
    private volatile string? _rootDisabledReason;

    public NodeAdminRuntime(IOptions<NodeAdminOptions> options, IConfiguration configuration, ILogger<NodeAdminRuntime> logger)
    {
        var value = options.Value;
        Options = value;
        Audience = ResolveAudience(value.Audience ?? configuration["PUBLIC_ORIGIN"], logger);
        RootKeyRevision = value.RootKeyRevision;
        var raw = (value.RootPublicKeys ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in raw)
        {
            if (!NodeAdminCrypto.TryDecodePublicKey(entry, out var key))
            {
                _rootDisabledReason = "NodeAdmin:RootPublicKeys contains a value that is not a base64url 32-byte Ed25519 public key.";
                continue;
            }
            _rootKeys.Add(new RootKey(key, NodeAdminCrypto.Fingerprint(key)));
        }
        if (_rootKeys.Count > 0 && RootKeyRevision < 1)
            _rootDisabledReason = "NodeAdmin:RootKeyRevision must be a positive integer when root keys are configured.";
        if (_rootDisabledReason is not null)
            logger.LogCritical("Node-admin root login is DISABLED: {Reason}", _rootDisabledReason);
    }

    public NodeAdminOptions Options { get; }

    /// <summary>The canonical origin clients sign; null when unconfigured or invalid (node-admin login refuses).</summary>
    public string? Audience { get; }

    public long RootKeyRevision { get; }

    /// <summary>True when at least one valid root key is configured and nothing disabled root login.</summary>
    public bool RootLoginEnabled => _rootKeys.Count > 0 && _rootDisabledReason is null;

    public string? RootDisabledReason => _rootDisabledReason;

    /// <summary>Disables root login for the life of the process (lower env revision than the durable watermark).</summary>
    public void DisableRootLogin(string reason) => _rootDisabledReason = reason;

    /// <summary>Exact 32-byte, fixed-time membership test against the configured root keys.</summary>
    public bool IsRootKey(ReadOnlySpan<byte> publicKey)
    {
        var match = false;
        foreach (var key in _rootKeys)
            match |= NodeAdminCrypto.FixedTimeEquals(key.PublicKey, publicKey);
        return match;
    }

    /// <summary>True while <paramref name="fingerprint"/> still names a configured root key.</summary>
    public bool IsRootFingerprint(string fingerprint) => _rootKeys.Any(key => string.Equals(key.Fingerprint, fingerprint, StringComparison.Ordinal));

    private static string? ResolveAudience(string? configured, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            && uri.Scheme is "https" or "http"
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.Equals(uri.GetLeftPart(UriPartial.Authority), configured, StringComparison.Ordinal))
            return configured;
        logger.LogCritical(
            "Node-admin login is DISABLED: NodeAdmin:Audience/PUBLIC_ORIGIN must be a bare origin such as https://node.example (no path, query or trailing slash).");
        return null;
    }

    private sealed record RootKey(byte[] PublicKey, string Fingerprint);
}
