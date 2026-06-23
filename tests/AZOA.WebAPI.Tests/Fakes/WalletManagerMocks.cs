using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Fakes;

/// <summary>
/// Shared <see cref="IWalletManager"/> mocks for the chain-capability gate
/// (economic-primitive-nodes D1). Existing quest tests drive Tier-1 / Condition
/// nodes that never trip the gate, so they use <see cref="Empty"/> (no wallet
/// bound) — the gate only fires for a Tier-2 handler.
/// </summary>
public static class WalletManagerMocks
{
    /// <summary>
    /// An <see cref="IWalletManager"/> whose <c>QueryAsync</c> returns an empty,
    /// non-error wallet list for any avatar ⇒ "no wallet bound" (gate fails closed
    /// for Tier-2 nodes; Tier-1 nodes are unaffected).
    /// </summary>
    public static IWalletManager Empty() => WithWallets();

    /// <summary>
    /// An <see cref="IWalletManager"/> whose <c>QueryAsync</c> returns the supplied
    /// wallets (default: none). A non-empty list ⇒ "wallet bound" so a Tier-2 node
    /// passes the gate.
    /// </summary>
    public static IWalletManager WithWallets(params IWallet[] wallets)
    {
        var mock = new Mock<IWalletManager>();
        mock.Setup(m => m.QueryAsync(
                It.IsAny<WalletQueryRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>>
            {
                IsError = false,
                Result = wallets,
            });
        return mock.Object;
    }

    /// <summary>One throwaway bound wallet so a Tier-2 gate check passes.</summary>
    public static IWalletManager WithOneWallet() =>
        WithWallets(new StubWallet { Id = Guid.NewGuid(), ChainType = "Algorand" });

    private sealed class StubWallet : IWallet
    {
        public Guid Id { get; set; }
        public Guid AvatarId { get; set; }
        public string ChainType { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string? PublicKey { get; set; }
        public string? Label { get; set; }
        public bool IsDefault { get; set; }
        public WalletType WalletType { get; set; }
        public string? EncryptedPrivateKey { get; set; }
        public string? EncryptedSeedPhrase { get; set; }
        public DateTime CreatedDate { get; set; }
    }
}
