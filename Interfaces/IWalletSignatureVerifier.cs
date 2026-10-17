namespace AZOA.WebAPI.Interfaces;

/// <summary>Verifies exact issued challenge bytes using the named chain's wallet signature format.</summary>
public interface IWalletSignatureVerifier
{
    /// <summary>Uses EIP-191 personal-message recovery for Ethereum and raw Ed25519 for Algorand/Solana; malformed or unsupported proofs return false.</summary>
    bool Verify(string chainType, string address, byte[] message, byte[] signature);
}
