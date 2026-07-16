using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Conformance;

/// <summary>Stores only the node signing key in a separately protected local file.</summary>
public sealed class ProtectedFileNodeIdentityKeyService : INodeIdentityKeyService
{
    private const string FileName = "node-identity.v1.protected";
    private readonly object _gate = new();
    private readonly IDataProtector _protector;
    private readonly IOptions<NodeConformanceOptions> _options;

    public ProtectedFileNodeIdentityKeyService(
        IDataProtectionProvider protectionProvider,
        IOptions<NodeConformanceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(protectionProvider);
        _protector = protectionProvider.CreateProtector("AZOA.NodeConformance.IdentityKey.v1");
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public NodeIdentityKeySnapshot GetCurrent()
    {
        lock (_gate)
        {
            var state = LoadOrCreate();
            return ToSnapshot(state);
        }
    }

    /// <inheritdoc/>
    public NodeIdentityKeySnapshot Rotate()
    {
        lock (_gate)
        {
            var oldState = LoadOrCreate();
            using var oldKey = ImportPrivateKey(oldState.CurrentPrivateKeyPkcs8Base64);
            using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var newPublicKey = ToPublicKey(newKey);
            var continuity = Convert.ToBase64String(oldKey.SignData(
                NodeConformanceCanonicalizer.ContinuitySigningBytes(newPublicKey),
                HashAlgorithmName.SHA256));
            var next = new ProtectedNodeIdentityKeyState(
                Convert.ToBase64String(newKey.ExportPkcs8PrivateKey()),
                new NodePreviousKeyContinuity(ToPublicKey(oldKey), continuity));
            Save(next);
            return ToSnapshot(next);
        }
    }

    private ProtectedNodeIdentityKeyState LoadOrCreate()
    {
        var path = GetPath();
        if (File.Exists(path))
            return Read(path);

        using var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var initial = new ProtectedNodeIdentityKeyState(
            Convert.ToBase64String(created.ExportPkcs8PrivateKey()),
            null);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(_protector.Protect(JsonSerializer.Serialize(initial)));
            return initial;
        }
        catch (IOException) when (File.Exists(path))
        {
            return Read(path);
        }
    }

    private ProtectedNodeIdentityKeyState Read(string path)
    {
        var plainText = _protector.Unprotect(File.ReadAllText(path));
        var state = JsonSerializer.Deserialize<ProtectedNodeIdentityKeyState>(plainText);
        if (state is null || string.IsNullOrWhiteSpace(state.CurrentPrivateKeyPkcs8Base64))
            throw new CryptographicException("The protected node identity file is invalid.");

        return state;
    }

    private void Save(ProtectedNodeIdentityKeyState state)
    {
        var path = GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var protectedText = _protector.Protect(JsonSerializer.Serialize(state));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, protectedText);
        File.Move(temporary, path, overwrite: true);
    }

    private string GetPath()
    {
        var directory = _options.Value.KeyStoragePath;
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("NodeConformance:KeyStoragePath is required when conformance is enabled.");

        return Path.Combine(Path.GetFullPath(directory), FileName);
    }

    private static NodeIdentityKeySnapshot ToSnapshot(ProtectedNodeIdentityKeyState state)
    {
        var signer = ImportPrivateKey(state.CurrentPrivateKeyPkcs8Base64);
        return new NodeIdentityKeySnapshot(signer, new NodeDescriptor(
            NodeId: string.Empty,
            CurrentKey: ToPublicKey(signer),
            PreviousKey: state.PreviousKey));
    }

    private static ECDsa ImportPrivateKey(string privateKeyPkcs8Base64)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out var bytesRead);
        if (bytesRead != Convert.FromBase64String(privateKeyPkcs8Base64).Length)
            throw new CryptographicException("The node identity private key has trailing data.");

        return key;
    }

    private static NodePublicKey ToPublicKey(ECDsa key)
    {
        var spki = key.ExportSubjectPublicKeyInfo();
        return new NodePublicKey(
            NodeConformanceCanonicalizer.Algorithm,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(spki)),
            Convert.ToBase64String(spki));
    }

    private sealed record ProtectedNodeIdentityKeyState(
        string CurrentPrivateKeyPkcs8Base64,
        NodePreviousKeyContinuity? PreviousKey);
}
