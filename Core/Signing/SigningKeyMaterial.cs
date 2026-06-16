using System.Security.Cryptography;

namespace OASIS.WebAPI.Core.Signing;

/// <summary>
/// Carries a decrypted private key as a <see cref="byte"/> array so the custody
/// layer can WIPE it after a single signing use (signing-core D2). A .NET
/// <c>string</c> is immutable and cannot be reliably zeroed, which is why the
/// signing seam never accepts a hex string for key material.
/// <para>
/// Lifecycle contract: the custody layer constructs this from a decrypted key,
/// hands it to <see cref="ITransactionSigner.Sign"/>, then <see cref="Dispose"/>s
/// it (or relies on the <c>using</c> pattern) to zero the buffer. The real
/// ownership-checked resolver + zeroing-on-decrypt path is the
/// <c>custody-key-management</c> track's deliverable; this type only defines the
/// seam and guarantees the buffer is zeroable here.
/// </para>
/// </summary>
public sealed class SigningKeyMaterial : IDisposable
{
    private readonly byte[] _privateKey;
    private bool _disposed;

    /// <param name="privateKey">
    /// The raw decrypted private key bytes. The array is COPIED defensively so the
    /// caller's buffer and this one can be zeroed independently.
    /// </param>
    public SigningKeyMaterial(byte[] privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        _privateKey = (byte[])privateKey.Clone();
    }

    /// <summary>
    /// The decrypted private key bytes. Throws once <see cref="Dispose"/> has zeroed
    /// the buffer so a use-after-free of secret material is a loud failure, not a
    /// silent read of zeros.
    /// </summary>
    public byte[] PrivateKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _privateKey;
        }
    }

    /// <summary>Zeroes the private-key buffer. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_privateKey);
        _disposed = true;
    }
}
