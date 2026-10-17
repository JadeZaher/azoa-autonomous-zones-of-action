using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Users;

/// <summary>F-H12: a tenant-acting lifecycle whose tenant can no longer continue it is taken over by the owner, and the tenant is locked out.</summary>
public sealed class NftOwnerTakeoverTests
{
    [Fact]
    public async Task The_owner_takes_over_a_tenant_lifecycle_and_the_tenant_can_no_longer_see_or_cancel_it()
    {
        var h = new NftLifecycleHarness();
        var user = Guid.NewGuid(); var wallet = h.AddWallet(user); var tenant = Guid.NewGuid();
        var credential = NftLifecycleHarness.Credential(user) with
        {
            TenantId = tenant, OwnerAuthority = false, Scopes = [AzoaScopes.NftMint], TokenUse = AzoaClaims.TokenUseChild,
        };
        var authority = new SigningAdmissionAuthority(1, "DirectJwt", credential, new(wallet.Id, user, wallet.Address, "Algorand"),
            [AzoaScopes.NftMint], [new(Guid.NewGuid(), AzoaScopes.NftMint, AzoaScopes.NftMint, DateTimeOffset.UtcNow, null)]);
        var command = new NftLifecycleCommand(user, user, wallet.Id, wallet.Id, wallet.Address, wallet.Address,
            NftLifecycleHarness.MintRequest(), -1, tenant, FeeQuote: new("Algorand", ChainNetwork.Devnet, "Mint", 1, "ALGO", 6, "0", "treasury", user),
            ActorAuthority: authority);
        var json = JsonSerializer.Serialize(command);
        var row = new NftLifecycle
        {
            Id = AZOA.WebAPI.Helpers.NftLifecycleIdentity.ForRequest(user, "Mint", "tenant-mint", tenant), ActorUserId = user.ToString("N"),
            RecipientUserId = user.ToString("N"), NftId = Guid.NewGuid().ToString("N"), Operation = "Mint", ChainType = "Algorand",
            Network = "Devnet", State = "Preparing", Stage = "Create", RequestJson = json,
            RequestHash = AZOA.WebAPI.Helpers.NftLifecycleIdentity.Fingerprint(json), CreatedAt = h.Clock.GetUtcNow(), UpdatedAt = h.Clock.GetUtcNow(),
        };
        (await h.Store.ClaimAsync(row)).Should().NotBeNull();
        var current = (await h.Store.GetAsync(row.Id))!;
        var request = new NftStageAcceptance(Guid.NewGuid(), current.Version, current.State, current.Stage, current.PreparationGeneration);

        (await h.Service.RenewActorAuthorityAsync(row.Id, request, NftLifecycleHarness.Credential(Guid.NewGuid())))
            .IsError.Should().BeTrue("only the owner may take over");
        var taken = await h.Service.RenewActorAuthorityAsync(row.Id, request, h.FreshCredential(user));

        taken.IsError.Should().BeFalse(taken.Message);
        (await h.Store.GetAsync(row.Id))!.RequestJson.Should().Be(json, "the original intent never changes");
        (await h.Service.GetAsync(user, row.Id, actingTenantId: tenant)).IsError.Should().BeTrue("the tenant is locked out");
        (await h.Service.CancelAsync(user, row.Id, actingTenantId: tenant, credential: credential)).IsError.Should().BeTrue();
        var again = new NftStageAcceptance(Guid.NewGuid(), (await h.Store.GetAsync(row.Id))!.Version, "Preparing", "Create", 0);
        (await h.Service.RenewActorAuthorityAsync(row.Id, again, credential)).IsError.Should().BeTrue("a tenant cannot renew back in");
        (await h.Service.GetAsync(user, row.Id)).IsError.Should().BeFalse("the owner now continues it outside any tenant");
        (await h.Service.CancelAsync(user, row.Id, credential: h.FreshCredential(user))).Result!.State.Should().Be("Cancelled");
    }
}
