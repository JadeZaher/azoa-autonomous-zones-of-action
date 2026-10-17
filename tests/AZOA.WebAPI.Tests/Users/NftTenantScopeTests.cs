using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Users;

/// <summary>Another tenant acting for the same user cannot discover or act on a lifecycle admitted under the first tenant.</summary>
public sealed class NftTenantScopeTests
{
    [Fact]
    public async Task SecondTenantCannotListGetRefreshOrCancelTheFirstTenantsLifecycle()
    {
        var h = new NftLifecycleHarness();
        var user = Guid.NewGuid(); var wallet = h.AddWallet(user);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var credential = NftLifecycleHarness.Credential(user) with
        {
            TenantId = first, OwnerAuthority = false, Scopes = [AzoaScopes.NftMint], TokenUse = AzoaClaims.TokenUseChild,
        };
        var authority = new SigningAdmissionAuthority(1, "DirectJwt", credential, new(wallet.Id, user, wallet.Address, "Algorand"),
            [AzoaScopes.NftMint], [new(Guid.NewGuid(), AzoaScopes.NftMint, AzoaScopes.NftMint, DateTimeOffset.UtcNow, null)]);
        var command = new NftLifecycleCommand(user, user, wallet.Id, wallet.Id, wallet.Address, wallet.Address,
            NftLifecycleHarness.MintRequest(), -1, first, FeeQuote: new("Algorand", ChainNetwork.Devnet, "Mint", 1, "ALGO", 6, "0", "treasury", user),
            ActorAuthority: authority);
        var json = JsonSerializer.Serialize(command);
        var row = new NftLifecycle
        {
            Id = AZOA.WebAPI.Helpers.NftLifecycleIdentity.ForRequest(user, "Mint", "first-tenant", first), ActorUserId = user.ToString("N"),
            RecipientUserId = user.ToString("N"), NftId = Guid.NewGuid().ToString("N"), Operation = "Mint", ChainType = "Algorand",
            Network = "Devnet", State = "Preparing", Stage = "Create", RequestJson = json,
            RequestHash = AZOA.WebAPI.Helpers.NftLifecycleIdentity.Fingerprint(json), CreatedAt = h.Clock.GetUtcNow(), UpdatedAt = h.Clock.GetUtcNow(),
        };
        (await h.Store.ClaimAsync(row)).Should().NotBeNull();

        (await h.Service.GetAsync(user, row.Id, actingTenantId: second)).IsError.Should().BeTrue();
        (await h.Service.ListPendingAsync(user, null, 25, actingTenantId: second)).Result!.Items.Should().BeEmpty();
        (await h.Service.RefreshAsync(user, row.Id, false, actingTenantId: second)).IsError.Should().BeTrue();
        (await h.Service.CancelAsync(user, row.Id, actingTenantId: second, credential: credential with { TenantId = second })).IsError.Should().BeTrue();

        (await h.Store.GetAsync(row.Id))!.State.Should().Be("Preparing");
        h.Prepared.Should().BeEmpty();
        (await h.Service.GetAsync(user, row.Id, actingTenantId: first)).IsError.Should().BeFalse();
        (await h.Service.ListPendingAsync(user, null, 25, actingTenantId: first)).Result!.Items.Should().ContainSingle()
            .Which.OperationId.Should().Be(row.Id);
    }

    [Fact]
    public async Task SecondTenantCannotRefundOrQuoteATransferCompletedUnderTheFirstTenant()
    {
        var h = new NftLifecycleHarness();
        var user = Guid.NewGuid(); var wallet = h.AddWallet(user);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var credential = NftLifecycleHarness.Credential(user) with
        {
            TenantId = first, OwnerAuthority = false, Scopes = [AzoaScopes.TransferSign], TokenUse = AzoaClaims.TokenUseChild,
        };
        var authority = new SigningAdmissionAuthority(1, "DirectJwt", credential, new(wallet.Id, user, wallet.Address, "Algorand"),
            [AzoaScopes.TransferSign], [new(Guid.NewGuid(), AzoaScopes.TransferSign, AzoaScopes.TransferSign, DateTimeOffset.UtcNow, null)]);
        var command = new NftLifecycleCommand(user, Guid.NewGuid(), wallet.Id, Guid.NewGuid(), wallet.Address, "holder",
            NftLifecycleHarness.MintRequest(), 0, first, FeeQuote: new("Algorand", ChainNetwork.Devnet, "Transfer", 1, "ALGO", 6, "0", "treasury", user),
            ActorAuthority: authority);
        var json = JsonSerializer.Serialize(command);
        var source = new NftLifecycle
        {
            Id = AZOA.WebAPI.Helpers.NftLifecycleIdentity.ForRequest(user, "Transfer", "first-tenant-transfer", first), ActorUserId = user.ToString("N"),
            RecipientUserId = command.Recipient.ToString("N"), NftId = Guid.NewGuid().ToString("N"), Operation = "Transfer", ChainType = "Algorand",
            Network = "Devnet", State = "Completed", Stage = "Transfer", RequestJson = json,
            RequestHash = AZOA.WebAPI.Helpers.NftLifecycleIdentity.Fingerprint(json), CreatedAt = h.Clock.GetUtcNow(), UpdatedAt = h.Clock.GetUtcNow(),
        };
        (await h.Store.ClaimAsync(source)).Should().NotBeNull();

        (await h.Service.RefundAsync(user, source.Id, "second-tenant-refund", actingTenantId: second,
            credential: credential with { TenantId = second })).IsError.Should().BeTrue();
        (await h.Service.QuoteFeeAsync(user, "Algorand", "Refund", sourceOperationId: source.Id, actingTenantId: second)).IsError.Should().BeTrue();
        (await h.Service.GetRequestAsync(user, "Refund", "second-tenant-refund", second)).IsError.Should().BeTrue("nothing was admitted");
        (await h.Service.QuoteFeeAsync(user, "Algorand", "Refund", sourceOperationId: source.Id, actingTenantId: first)).IsError.Should().BeFalse();
    }
}
