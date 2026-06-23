namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// Verifies that a wallet signature over the challenge message bytes was produced by
/// the private key controlling <c>address</c> on <c>chainType</c>
/// (user-sovereign-identity §1, AC1). Mockable so the manager's auth flow can be
/// unit-tested without real curve math.
///
/// <para>Algorand (ed25519) is the first supported chain (AC1). Other chains throw
/// <see cref="NotSupportedException"/> per the spec out-of-scope note — EVM/Solana
/// follow the provider signature-verify pattern in a later track. The verifier is
/// FAIL-CLOSED: any malformed address/signature yields <c>false</c>, never an
/// exception a caller could mistake for "valid".</para>
/// </summary>
public interface IWalletSignatureVerifier
{
    /// <summary>
    /// True iff <paramref name="signature"/> is a valid signature over
    /// <paramref name="message"/> for the key controlling <paramref name="address"/>
    /// on <paramref name="chainType"/>. Returns <c>false</c> on any malformed input.
    /// Throws <see cref="NotSupportedException"/> for an unsupported chain type.
    /// </summary>
    bool Verify(string chainType, string address, byte[] message, byte[] signature);
}
