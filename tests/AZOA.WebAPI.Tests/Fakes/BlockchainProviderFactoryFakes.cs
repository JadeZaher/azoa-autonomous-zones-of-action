using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using Moq;

namespace AZOA.WebAPI.Tests.Fakes;

/// <summary>
/// Test doubles for <see cref="IBlockchainProviderFactory"/>, used by the durable
/// quest-engine harnesses since <see cref="AZOA.WebAPI.Services.Quest.Workflow.QuestNodeStepHandler"/>
/// gained a factory dependency for reconcile-before-retry
/// (blockchain-recovery-and-portable-wallets §1.4).
/// </summary>
public static class BlockchainProviderFactoryFakes
{
    /// <summary>
    /// A factory whose provider returns the supplied <paramref name="confirmation"/>
    /// for <c>GetTransactionConfirmationAsync</c>. Defaults to
    /// <see cref="ChainConfirmation.Unknown"/> — the conservative verdict — so the
    /// engine's existing Tier-1 / non-chain tests (which never reach the
    /// reconcile branch because their nodes don't fail with a tx hash) are
    /// unaffected.
    /// </summary>
    public static IBlockchainProviderFactory Returning(
        ChainConfirmation confirmation = ChainConfirmation.Unknown)
    {
        var provider = new Mock<IBlockchainProvider>();
        provider.SetupGet(p => p.ChainType).Returns("Algorand");
        provider
            .Setup(p => p.GetTransactionConfirmationAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ChainConfirmation> { Result = confirmation });

        var factory = new Mock<IBlockchainProviderFactory>();
        factory.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
            .Returns(provider.Object);
        factory.Setup(f => f.GetDefaultProvider()).Returns(provider.Object);
        return factory.Object;
    }
}
