// SPDX-License-Identifier: UNLICENSED
//
// Phase 1B federated sign-in — the three security-critical rules of D17.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Auth;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Auth;
using AZOA.WebAPI.Services.Kyc;

namespace AZOA.WebAPI.Tests.Auth;

public class ExternalLoginServiceTests
{
    private const string VictimEmail = "victim@example.com";

    // ── Rule 1: an OAuth identity is NEVER auto-linked by email ───────────────

    /// <summary>
    /// The account pre-hijacking scenario, end to end: an attacker has already
    /// registered a password account on the victim's email address. The victim then
    /// signs in with Google for the first time. The two MUST NOT merge.
    /// </summary>
    [Fact]
    public async Task Google_sign_in_never_merges_into_a_password_account_with_the_same_email()
    {
        var attackerAccount = new AZOA.WebAPI.Models.User
        {
            Id = Guid.NewGuid(),
            Username = "attacker",
            Email = VictimEmail,
            PasswordHash = "attacker-chosen-hash",
        };

        var harness = new Harness(seedUsers: [attackerAccount]);

        // The victim's Google account asserts the SAME email the attacker squatted.
        var identity = new ExternalIdentity(
            ExternalAuthProvider.GOOGLE,
            Subject: "google-subject-1234567890",
            Email: VictimEmail,
            DisplayName: "Victim");

        var resolved = await harness.Service.ResolveOrCreateAsync(identity, tenantId: null);

        resolved.IsError.Should().BeFalse();
        resolved.Result.Should().NotBeNull();

        // The decisive assertion: a brand-new user, NOT the attacker's.
        resolved.Result!.Id.Should().NotBe(attackerAccount.Id,
            "merging on a matching email is the account pre-hijacking vector D17 forbids");

        // And the new account did not inherit the email either.
        resolved.Result.Email.Should().NotBe(VictimEmail);
        resolved.Result.Email.Should().EndWith("@oauth.azoa.local");
    }

    /// <summary>
    /// The structural half of rule 1: the resolve path must never even ASK the user
    /// store for a list it could search by email. GetAllAsync is the only method that
    /// could serve an email lookup, so its call count is the proof.
    /// </summary>
    [Fact]
    public async Task Resolving_a_federated_identity_never_enumerates_users_by_email()
    {
        var harness = new Harness(seedUsers:
        [
            new AZOA.WebAPI.Models.User
            {
                Id = Guid.NewGuid(), Username = "someone", Email = VictimEmail, PasswordHash = "x",
            }
        ]);

        await harness.Service.ResolveOrCreateAsync(
            new ExternalIdentity(ExternalAuthProvider.GOOGLE, "subject-abc", Email: VictimEmail),
            tenantId: null);

        harness.UserStore.Verify(
            store => store.GetAllAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "an email-based lookup is the pre-hijacking vector; the resolve path must not have one");
    }

    /// <summary>
    /// A provider that hands back an email-shaped subject is refused outright, so a
    /// mis-mapped claim action can never turn the mutable email INTO the join key.
    /// </summary>
    [Fact]
    public void An_email_shaped_subject_is_refused_as_an_identity()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.NameIdentifier, VictimEmail),
            ], "test"));

        ExternalIdentityReader.Read(ExternalAuthProvider.GOOGLE, principal)
            .Should().BeNull("the subject must be the provider's stable id, never the email");
    }

    /// <summary>Linking is the ONLY route from a federated identity to an existing user.</summary>
    [Fact]
    public async Task Linking_requires_an_authenticated_user_and_attaches_to_exactly_that_account()
    {
        var existing = new AZOA.WebAPI.Models.User
        {
            Id = Guid.NewGuid(), Username = "real-user", Email = VictimEmail, PasswordHash = "hash",
        };
        var harness = new Harness(seedUsers: [existing]);

        var linked = await harness.Service.LinkToUserAsync(
            new ExternalIdentity(ExternalAuthProvider.GOOGLE, "subject-xyz", Email: VictimEmail),
            existing.Id,
            tenantId: null);

        linked.IsError.Should().BeFalse();
        linked.Result!.UserId.Should().Be(existing.Id.ToString("N"));

        // A second user cannot steal an already-linked provider account.
        var thief = new AZOA.WebAPI.Models.User
        {
            Id = Guid.NewGuid(), Username = "thief", Email = "thief@example.com", PasswordHash = "hash",
        };
        harness.SeedUser(thief);

        var stolen = await harness.Service.LinkToUserAsync(
            new ExternalIdentity(ExternalAuthProvider.GOOGLE, "subject-xyz"),
            thief.Id,
            tenantId: null);

        stolen.IsError.Should().BeTrue();
    }

    /// <summary>An authenticated caller with no user identity cannot link anything.</summary>
    [Fact]
    public async Task Linking_without_an_user_identity_is_refused()
    {
        var harness = new Harness();

        var linked = await harness.Service.LinkToUserAsync(
            new ExternalIdentity(ExternalAuthProvider.GITHUB, "gh-1"),
            Guid.Empty,
            tenantId: null);

        linked.IsError.Should().BeTrue();
    }

    // ── Rule 3: the binding is PER TENANT ─────────────────────────────────────

    /// <summary>
    /// The same Google account signing in to two different consumers must land on two
    /// unlinkable users. A node-global binding would collapse them into one identity
    /// — the cross-tenant failure D1/D23 exist to prevent, and the one
    /// kyc_screening_monitor's uniqueness note calls out explicitly.
    /// </summary>
    [Fact]
    public async Task The_same_provider_subject_in_two_tenants_yields_two_distinct_users()
    {
        var harness = new Harness();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var identity = new ExternalIdentity(
            ExternalAuthProvider.GOOGLE, "one-google-account", Email: "person@example.com");

        var inTenantA = await harness.Service.ResolveOrCreateAsync(identity, tenantA);
        var inTenantB = await harness.Service.ResolveOrCreateAsync(identity, tenantB);

        inTenantA.IsError.Should().BeFalse();
        inTenantB.IsError.Should().BeFalse();

        inTenantA.Result!.Id.Should().NotBe(inTenantB.Result!.Id,
            "one provider account must resolve to a separate user per tenant");

        harness.Bindings.Should().HaveCount(2);
        harness.Bindings.Select(binding => binding.TenantScope).Should()
            .OnlyHaveUniqueItems("each binding lives in its own uniqueness scope");

        // And the scope is computed by the SAME rule kyc_identity uses.
        harness.Bindings.Select(binding => binding.TenantScope).Should().BeEquivalentTo(
        [
            KycPersonKeyDeriver.ScopeValue(tenantA),
            KycPersonKeyDeriver.ScopeValue(tenantB),
        ]);
    }

    /// <summary>Signing in twice under one tenant converges on the SAME user.</summary>
    [Fact]
    public async Task Repeated_sign_in_within_one_tenant_resolves_to_the_same_user()
    {
        var harness = new Harness();
        var tenant = Guid.NewGuid();
        var identity = new ExternalIdentity(ExternalAuthProvider.GITHUB, "gh-42");

        var first = await harness.Service.ResolveOrCreateAsync(identity, tenant);
        var second = await harness.Service.ResolveOrCreateAsync(identity, tenant);

        second.Result!.Id.Should().Be(first.Result!.Id);
        harness.Bindings.Should().HaveCount(1);
    }

    // ── A revoked provider account must not orphan the user ─────────────────

    /// <summary>Unlinking removes the binding and leaves the user row intact.</summary>
    [Fact]
    public async Task Unlinking_a_provider_never_deletes_the_user()
    {
        var user = new AZOA.WebAPI.Models.User
        {
            Id = Guid.NewGuid(),
            Username = "has-password",
            Email = "user@example.com",
            // A real password credential, so the account stays reachable after unlink.
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Sup3rSecret"),
        };
        var harness = new Harness(seedUsers: [user]);

        await harness.Service.LinkToUserAsync(
            new ExternalIdentity(ExternalAuthProvider.GOOGLE, "sub-1"), user.Id, null);

        var unlinked = await harness.Service.UnlinkAsync(ExternalAuthProvider.GOOGLE, user.Id, null);

        unlinked.IsError.Should().BeFalse();
        harness.Bindings.Should().BeEmpty();

        harness.UserStore.Verify(
            store => store.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "unlinking a provider must never cascade to the user");

        var stillThere = await harness.UserStore.Object.GetByIdAsync(user.Id);
        stillThere.Result.Should().NotBeNull();
    }

    /// <summary>
    /// Removing the LAST way into an account is refused. The user would survive the
    /// delete but nobody could ever reach it again, which is orphaning by another name.
    /// </summary>
    [Fact]
    public async Task Unlinking_the_only_remaining_credential_is_refused()
    {
        var harness = new Harness();

        var created = await harness.Service.ResolveOrCreateAsync(
            new ExternalIdentity(ExternalAuthProvider.GOOGLE, "only-credential"), tenantId: null);

        var unlinked = await harness.Service.UnlinkAsync(
            ExternalAuthProvider.GOOGLE, created.Result!.Id, tenantId: null);

        unlinked.IsError.Should().BeTrue(
            "an OAuth-created user has no password its owner knows; removing the "
            + "binding would strand the account");
        harness.Bindings.Should().HaveCount(1);
    }

    /// <summary>A binding whose user has vanished fails closed instead of re-creating one.</summary>
    [Fact]
    public async Task A_binding_pointing_at_a_missing_user_fails_closed()
    {
        var harness = new Harness();
        harness.Bindings.Add(new UserExternalLogin
        {
            Id = "orphan",
            TenantScope = KycPersonKeyDeriver.ScopeValue(null),
            Provider = ExternalAuthProvider.GOOGLE,
            Subject = "ghost",
            UserId = Guid.NewGuid().ToString("N"),
        });

        var resolved = await harness.Service.ResolveOrCreateAsync(
            new ExternalIdentity(ExternalAuthProvider.GOOGLE, "ghost"), tenantId: null);

        resolved.IsError.Should().BeTrue(
            "silently minting a replacement would hand the provider account a fresh "
            + "identity carrying none of the original's history");
    }

    /// <summary>A deactivated user cannot be signed into through a federated provider.</summary>
    [Fact]
    public async Task A_deactivated_user_cannot_sign_in()
    {
        var user = new AZOA.WebAPI.Models.User
        {
            Id = Guid.NewGuid(),
            Username = "disabled",
            Email = "disabled@example.com",
            PasswordHash = "hash",
            IsActive = false,
        };
        var harness = new Harness(seedUsers: [user]);
        harness.Bindings.Add(new UserExternalLogin
        {
            Id = "b1",
            TenantScope = KycPersonKeyDeriver.ScopeValue(null),
            Provider = ExternalAuthProvider.GOOGLE,
            Subject = "sub-disabled",
            UserId = user.Id.ToString("N"),
        });

        var resolved = await harness.Service.ResolveOrCreateAsync(
            new ExternalIdentity(ExternalAuthProvider.GOOGLE, "sub-disabled"), tenantId: null);

        resolved.IsError.Should().BeTrue();
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// In-memory doubles for the two stores. The external-login double indexes exactly
    /// as the real UNIQUE index does — on (tenant_scope, provider, subject) — so a test
    /// that passes here would also pass against the committed .surql.
    /// </summary>
    private sealed class Harness
    {
        public Mock<IUserStore> UserStore { get; } = new(MockBehavior.Strict);

        public List<UserExternalLogin> Bindings { get; } = [];

        public ExternalLoginService Service { get; }

        private readonly Dictionary<Guid, IUser> _users = [];

        public Harness(IEnumerable<AZOA.WebAPI.Models.User>? seedUsers = null)
        {
            foreach (var user in seedUsers ?? [])
                _users[user.Id] = user;

            UserStore
                .Setup(store => store.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => _users.TryGetValue(id, out var found)
                    ? AZOAResult<IUser>.Success(found)
                    : AZOAResult<IUser>.Failure("User not found."));

            UserStore
                .Setup(store => store.CreateIfAbsentAsync(It.IsAny<IUser>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IUser user, CancellationToken _) =>
                {
                    if (_users.TryGetValue(user.Id, out var existing))
                        return AZOAResult<IUser>.Success(existing);

                    // The real store's global UNIQUE index on user.email. If the
                    // service ever reused a provider address, this would throw and the
                    // cross-tenant test would fail — which is the point.
                    if (_users.Values.Any(candidate =>
                            string.Equals(candidate.Email, user.Email, StringComparison.OrdinalIgnoreCase)))
                    {
                        return AZOAResult<IUser>.Failure("Duplicate email (UNIQUE index).");
                    }

                    _users[user.Id] = user;
                    return AZOAResult<IUser>.Success(user);
                });

            var loginStore = new Mock<IUserExternalLoginStore>(MockBehavior.Strict);

            loginStore
                .Setup(store => store.GetBySubjectAsync(
                    It.IsAny<string>(), It.IsAny<ExternalAuthProvider>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string scope, ExternalAuthProvider provider, string subject, CancellationToken _) =>
                    AZOAResult<UserExternalLogin?>.Success(Bindings.FirstOrDefault(binding =>
                        binding.TenantScope == scope
                        && binding.Provider == provider
                        && binding.Subject == subject)));

            loginStore
                .Setup(store => store.CreateAsync(
                    It.IsAny<UserExternalLogin>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((UserExternalLogin binding, CancellationToken _) =>
                {
                    var collides = Bindings.Any(existing =>
                        existing.TenantScope == binding.TenantScope
                        && existing.Provider == binding.Provider
                        && existing.Subject == binding.Subject);
                    if (collides)
                        return AZOAResult<UserExternalLogin>.Failure("UNIQUE index violation.");

                    Bindings.Add(binding);
                    return AZOAResult<UserExternalLogin>.Success(binding);
                });

            loginStore
                .Setup(store => store.GetByUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string userId, CancellationToken _) =>
                    AZOAResult<IReadOnlyList<UserExternalLogin>>.Success(
                        Bindings.Where(binding => binding.UserId == userId).ToList()));

            loginStore
                .Setup(store => store.TouchLastLoginAsync(
                    It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<bool>.Success(true));

            loginStore
                .Setup(store => store.DeleteAsync(
                    It.IsAny<string>(), It.IsAny<ExternalAuthProvider>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string scope, ExternalAuthProvider provider, string userId, CancellationToken _) =>
                    AZOAResult<bool>.Success(
                        Bindings.RemoveAll(binding =>
                            binding.TenantScope == scope
                            && binding.Provider == provider
                            && binding.UserId == userId) > 0));

            Service = new ExternalLoginService(
                UserStore.Object,
                loginStore.Object,
                NullLogger<ExternalLoginService>.Instance);
        }

        public void SeedUser(AZOA.WebAPI.Models.User user) => _users[user.Id] = user;
    }
}
