using System.Text;
using FluentAssertions;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Xunit;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// Proves the signer seam is chain-agnostic: the factory resolves a Solana signer
/// (so the contract is honest for a registered-but-unimplemented chain) while the
/// Solana stub fails CLOSED — an error result, never a silent no-op — for a
/// value-moving primitive. (deploy-stub H1)
/// </summary>
public class SolanaSignerStubTests
{
    private static ITransactionSignerFactory BuildFactory() =>
        new TransactionSignerFactory(new ITransactionSigner[]
        {
            new AlgorandTransactionSigner(),
            new SolanaTransactionSigner(),
        });

    [Fact]
    public void Factory_resolves_Solana_signer_without_throwing()
    {
        var factory = BuildFactory();

        factory.TryGetSigner("Solana", out var signer).Should().BeTrue();
        signer.Should().BeOfType<SolanaTransactionSigner>();
        signer!.ChainType.Should().Be("Solana");
    }

    [Fact]
    public void Factory_resolves_signers_case_insensitively_by_chain_type()
    {
        var factory = BuildFactory();

        factory.GetSigner("algorand").Should().BeOfType<AlgorandTransactionSigner>();
        factory.GetSigner("SOLANA").Should().BeOfType<SolanaTransactionSigner>();
    }

    [Fact]
    public void Solana_sign_fails_closed_with_no_signed_bytes()
    {
        var signer = new SolanaTransactionSigner();
        using var key = new SigningKeyMaterial(System.Text.Encoding.UTF8.GetBytes("not-a-real-key"));

        var result = signer.Sign(System.Text.Encoding.UTF8.GetBytes("canonical-txn"), key);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeNull();
        result.Message.Should().Contain("not yet implemented");
    }
}
