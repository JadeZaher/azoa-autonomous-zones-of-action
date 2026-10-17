namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Checks the full serialized packet budget, including short-vector signature count and signatures.</summary>
public static class SolanaPacketBudget
{
    public static bool Fits(int messageLength, int requiredSignatures)
        => messageLength > 0 && requiredSignatures is > 0 and <= byte.MaxValue &&
            (long)messageLength + (requiredSignatures < 128 ? 1 : 2) + 64L * requiredSignatures <= 1232;
}
