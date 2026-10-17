using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Signing;

/// <summary>Produces a verified detached signature for a declared required signer of a canonical transaction message.</summary>
public interface IPartialTransactionSigner
{
    /// <summary>Rejects a key or signer absent from the canonical message; caller retains custody and envelope assembly.</summary>
    AZOAResult<byte[]> SignPartial(byte[] canonicalMessage, string expectedSigner, SigningKeyMaterial key);
}
