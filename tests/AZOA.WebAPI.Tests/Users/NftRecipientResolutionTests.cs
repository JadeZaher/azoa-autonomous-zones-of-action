using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Users;

/// <summary>RV-L4-02: an unproven duplicate wallet row for an address can neither block a transfer nor capture the NFT.</summary>
public sealed class NftRecipientResolutionTests
{
    private const string Address = "shared-recipient-address";

    [Theory]
    [InlineData("custodial")]
    [InlineData("signature-proven")]
    public async Task An_unproven_duplicate_row_does_not_stop_the_transfer_reaching_the_real_owner(string ownerWallet)
    {
        var (h, holder, nftId) = await HeldNftAsync();
        var alice = Guid.NewGuid();
        var mallory = Guid.NewGuid();
        h.AddWallet(mallory, Address, external: true);
        var owned = h.AddWallet(alice, Address, external: ownerWallet == "signature-proven");
        if (ownerWallet == "signature-proven") Prove(h, alice, owned.Id);

        var moved = await h.Service.MoveAsync(holder, nftId, Address, false, "to-alice", credential: NftLifecycleHarness.Credential(holder));

        moved.IsError.Should().BeFalse(moved.Message);
        moved.Result!.State.Should().Be("Completed");
        moved.Result.RecipientUserId.Should().Be(alice);
        (await h.Store.GetAuthorityAsync(nftId.ToString("N")))!.HolderUserId.Should().Be(alice.ToString("N"));
    }

    [Fact]
    public async Task A_custodial_wallet_stored_with_a_lowercase_chain_name_still_receives_the_transfer()
    {
        var (h, holder, nftId) = await HeldNftAsync();
        var alice = Guid.NewGuid();
        h.AddWallet(alice, Address, storedChain: "algorand");

        var moved = await h.Service.MoveAsync(holder, nftId, Address, false, "lowercase-chain", credential: NftLifecycleHarness.Credential(holder));

        moved.IsError.Should().BeFalse(moved.Message);
        moved.Result!.RecipientUserId.Should().Be(alice);
    }

    [Theory]
    [InlineData("algorand", "Algorand", "algorand")]
    [InlineData("Solana", "Solana", "solana")]
    [InlineData("ETHEREUM", "Ethereum", "ethereum")]
    public void Lookup_probes_the_given_canonical_and_lowercase_chain_spellings(string given, string canonical, string lower)
        => NftRecipientLookupForms.For(given, Address).Select(form => form.Chain).Distinct()
            .Should().BeEquivalentTo(new[] { given, canonical, lower }.Distinct());

    [Fact]
    public void Case_sensitive_addresses_are_never_folded_and_ethereum_adds_its_canonical_forms()
    {
        const string solana = "9xQeWvG816bUx9EPjHmaT23yvVM2ZWbrrpZb9PusVFin";
        NftRecipientLookupForms.For("Solana", solana).Select(form => form.Address).Distinct().Should().Equal(solana);
        var ethereum = NftRecipientLookupForms.For("Ethereum", "0x52908400098527886E0F7030069857D2E4169EE7")
            .Select(form => form.Address).Distinct().ToArray();
        ethereum.Should().Contain("0x52908400098527886e0f7030069857d2e4169ee7").And.Contain("0x52908400098527886E0F7030069857D2E4169EE7");
    }

    [Fact]
    public async Task An_attacker_who_registered_the_address_first_without_proof_is_refused_and_never_recorded_as_owner()
    {
        var (h, holder, nftId) = await HeldNftAsync();
        var mallory = Guid.NewGuid();
        h.AddWallet(mallory, Address, external: true);
        var signatures = h.Prepared.Count;

        var moved = await h.Service.MoveAsync(holder, nftId, Address, false, "to-mallory", credential: NftLifecycleHarness.Credential(holder));

        moved.IsError.Should().BeTrue();
        (await h.Store.GetAsync(NftLifecycleIdentity.ForRequest(holder, "Transfer", "to-mallory"))).Should().BeNull("nothing is admitted");
        (await h.Store.GetAuthorityAsync(nftId.ToString("N")))!.HolderUserId.Should().Be(holder.ToString("N"));
        h.Prepared.Should().HaveCount(signatures);
    }

    [Fact]
    public async Task An_address_claimed_by_two_accounts_is_a_typed_refusal_not_a_server_error()
    {
        var (h, holder, nftId) = await HeldNftAsync();
        var alice = Guid.NewGuid();
        var mallory = Guid.NewGuid();
        h.AddWallet(alice, Address);
        Prove(h, mallory, h.AddWallet(mallory, Address, external: true).Id);

        var moved = await h.Service.MoveAsync(holder, nftId, Address, false, "ambiguous", credential: NftLifecycleHarness.Credential(holder));

        moved.IsError.Should().BeTrue();
        moved.Code.Should().Be("NFT_RECIPIENT_AMBIGUOUS");
        (await h.Store.GetAsync(NftLifecycleIdentity.ForRequest(holder, "Transfer", "ambiguous"))).Should().BeNull();
    }

    private static void Prove(NftLifecycleHarness h, Guid user, Guid walletId)
        => h.OwnershipProofs.Setup(store => store.GetAsync(h.ChainType, Address, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WalletOwnershipProof(user, walletId, h.ChainType, Address, DateTime.UtcNow));

    private static async Task<(NftLifecycleHarness Harness, Guid Holder, Guid NftId)> HeldNftAsync()
    {
        var h = new NftLifecycleHarness();
        var holder = Guid.NewGuid();
        h.AddWallet(holder);
        var minted = (await h.Service.MintAsync(holder, holder, NftLifecycleHarness.MintRequest(), "held",
            credential: NftLifecycleHarness.Credential(holder))).Result!;
        minted.State.Should().Be("Completed");
        return (h, holder, minted.NftId);
    }
}
