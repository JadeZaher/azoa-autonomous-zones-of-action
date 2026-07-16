using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Services.Conformance;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>Persists the last signed public-history checkpoint beside the dedicated node identity.</summary>
public sealed class NodeTransparencyHistoryCheckpointStore
{
    private const string FileName = "node-transparency-history.v1.protected";
    private readonly object _gate = new();
    private readonly IDataProtector _protector;
    private readonly IOptions<NodeConformanceOptions> _identityOptions;

    public NodeTransparencyHistoryCheckpointStore(
        IDataProtectionProvider protectionProvider,
        IOptions<NodeConformanceOptions> identityOptions)
    {
        ArgumentNullException.ThrowIfNull(protectionProvider);
        _protector = protectionProvider.CreateProtector("AZOA.NodeTransparency.HistoryCheckpoint.v1");
        _identityOptions = identityOptions ?? throw new ArgumentNullException(nameof(identityOptions));
    }

    /// <summary>Loads the previously protected checkpoint, if this node has made one.</summary>
    public NodeTransparencyHistoryCheckpoint? Get()
    {
        lock (_gate)
        {
            var path = GetPath();
            if (!File.Exists(path))
                return null;

            var value = JsonSerializer.Deserialize<NodeTransparencyHistoryCheckpoint>(
                _protector.Unprotect(File.ReadAllText(path)));
            return value ?? throw new CryptographicException("The protected node transparency checkpoint is invalid.");
        }
    }

    /// <summary>Atomically replaces the protected local checkpoint after an exact-chain extension.</summary>
    public void Save(NodeTransparencyHistoryCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (_gate)
        {
            var path = GetPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, _protector.Protect(JsonSerializer.Serialize(checkpoint)));
            File.Move(temporary, path, overwrite: true);
        }
    }

    private string GetPath()
    {
        var directory = _identityOptions.Value.KeyStoragePath;
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("NodeConformance:KeyStoragePath is required for signed audit checkpoints.");

        return Path.Combine(Path.GetFullPath(directory), FileName);
    }
}
