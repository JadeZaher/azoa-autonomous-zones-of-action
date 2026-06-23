using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>
/// Fail-closed Solana signer stub (signing-core-keystone follow-up; deploy-stub
/// H1 — Solana/Ethereum real keygen + signing).
/// <para>
/// The signer seam (<see cref="ITransactionSigner"/> +
/// <c>TransactionSignerFactory</c>) is chain-agnostic: the Algorand impl keeps
/// all its chain specifics to itself. This stub exists so the factory's contract
/// is honest for Solana — <c>GetSigner("Solana")</c> RESOLVES rather than throwing
/// "no signer registered", which lets a caller probe the seam — while
/// <see cref="Sign"/> FAILS LOUDLY with no side effect. A silent no-op would be
/// the dangerous outcome for a value-moving primitive; an explicit error
/// <see cref="AZOAResult{T}"/> is not. Mirrors the Veriff KYC provider stub.
/// </para>
/// <para>
/// To make Solana real, replace the body of <see cref="Sign"/> with an Ed25519
/// signing path over the canonical Solana message bytes (e.g. via
/// <c>Solana.Wallet</c>, already referenced) and drop this XML note — the seam,
/// factory, and DI line stay exactly as they are.
/// </para>
/// </summary>
public sealed class SolanaTransactionSigner : ITransactionSigner
{
    public string ChainType => "Solana";

    public AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key) =>
        new()
        {
            IsError = true,
            Message = "Solana transaction signing is not yet implemented "
                    + "(deploy-stub H1 — see conductor/DEPLOY-STEPS-TODO.md). "
                    + "The signer seam is registered so the factory resolves, but "
                    + "no Solana transaction is signed or submitted.",
        };
}
