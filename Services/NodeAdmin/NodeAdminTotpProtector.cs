using Microsoft.AspNetCore.DataProtection;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Encrypts TOTP secrets with the node DataProtection key ring, bound to the owning admin id.</summary>
public sealed class NodeAdminTotpProtector(IDataProtectionProvider provider)
{
    private const string Purpose = "AZOA.NodeAdmin.Totp.v1";

    public string Protect(string adminRecordId, byte[] secret)
        => Convert.ToBase64String(For(adminRecordId).Protect(secret));

    /// <summary>Decrypts a secret; a ciphertext from another admin or key ring throws <see cref="System.Security.Cryptography.CryptographicException"/>.</summary>
    public byte[] Unprotect(string adminRecordId, string ciphertext)
        => For(adminRecordId).Unprotect(Convert.FromBase64String(ciphertext));

    private IDataProtector For(string adminRecordId) => provider.CreateProtector(Purpose, adminRecordId);
}
