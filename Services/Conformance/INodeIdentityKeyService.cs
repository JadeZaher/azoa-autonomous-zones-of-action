using System.Security.Cryptography;

namespace AZOA.WebAPI.Services.Conformance;

/// <summary>Owns the dedicated local node-identity signing key, never a chain wallet key.</summary>
public interface INodeIdentityKeyService
{
    /// <summary>Loads or creates the current identity key and public rotation chain.</summary>
    NodeIdentityKeySnapshot GetCurrent();

    /// <summary>Replaces the current identity key and proves continuity with the old key.</summary>
    NodeIdentityKeySnapshot Rotate();
}

/// <summary>Usable private signer with public descriptor data.</summary>
public sealed class NodeIdentityKeySnapshot : IDisposable
{
    private readonly ECDsa _signer;

    internal NodeIdentityKeySnapshot(ECDsa signer, NodeDescriptor descriptor)
    {
        _signer = signer;
        Descriptor = descriptor;
    }

    /// <summary>Public descriptor for the current signer.</summary>
    public NodeDescriptor Descriptor { get; }

    /// <summary>Signs domain-separated canonical bytes with the local identity key.</summary>
    public byte[] Sign(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _signer.SignData(payload, HashAlgorithmName.SHA256);
    }

    /// <inheritdoc/>
    public void Dispose() => _signer.Dispose();
}
