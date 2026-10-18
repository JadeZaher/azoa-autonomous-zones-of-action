// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Auth;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using UserExternalLogin = AZOA.WebAPI.Persistence.SurrealDb.Models.UserExternalLogin;
using AZOA.WebAPI.Services.Auth;
using AZOA.WebAPI.Services.Kyc;

namespace AZOA.WebAPI.Tests.Auth;

/// <summary>
/// QA H-A (owner decision 2026-10-18): first wallet sign-in and first federated sign-in create an
/// account, so both obey the node's self-registration switch; an already-bound identity always signs in.
/// </summary>
public sealed class AccountCreationPolicyTests
{
    private static readonly string Address = new Algorand.Address(new byte[32]).ToString();
    private const string Chain = "algorand";

    public static TheoryData<string, string?, bool> NodePolicies => new()
    {
        // environment, AllowPublicSelfServiceInProduction, new account admitted
        { Environments.Production, null, false },
        { Environments.Production, "false", false },
        { Environments.Production, "true", true },
        { Environments.Development, null, true },
    };

    [Theory]
    [MemberData(nameof(NodePolicies))]
    public async Task First_wallet_sign_in_creates_an_account_only_where_self_registration_is_open(
        string environment, string? acknowledgement, bool admitted)
    {
        var wallet = new WalletHarness(environment, acknowledgement);

        var response = await wallet.Controller.Verify(wallet.Request);

        if (admitted)
        {
            response.Result.Should().BeOfType<OkObjectResult>();
            wallet.Users.Should().ContainSingle(user => user.AuthWalletAddress == Address);
        }
        else
        {
            var refusal = response.Result.Should().BeOfType<ObjectResult>().Subject;
            refusal.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            refusal.Value.Should().BeOfType<AZOAResult<WalletAuthTokenResponse>>()
                .Which.Code.Should().Be(SelfRegistrationPolicy.DisabledCode);
            wallet.Users.Should().BeEmpty("a closed node must not mint an account from a wallet proof");
        }
    }

    [Fact]
    public async Task A_wallet_already_bound_to_an_account_signs_in_on_a_closed_node()
    {
        var wallet = new WalletHarness(Environments.Production, acknowledgement: null);
        var existing = new User { Id = Guid.NewGuid(), Username = "wallet-algorand-holder", IsActive = true, AuthWalletAddress = Address, AuthWalletChainType = Chain };
        wallet.Users.Add(existing);

        var response = await wallet.Controller.Verify(wallet.Request);

        response.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<AZOAResult<WalletAuthTokenResponse>>()
            .Which.Result!.UserId.Should().Be(existing.Id);
        wallet.Users.Should().ContainSingle();
    }

    [Fact]
    public async Task A_bad_wallet_signature_on_a_closed_node_stays_a_generic_401()
    {
        var wallet = new WalletHarness(Environments.Production, acknowledgement: null, signatureValid: false);

        var response = await wallet.Controller.Verify(wallet.Request);

        response.Result.Should().BeOfType<UnauthorizedObjectResult>(
            "the registration refusal is only reachable after a valid proof, so it cannot become a probe");
    }

    [Theory]
    [MemberData(nameof(NodePolicies))]
    public async Task First_federated_sign_in_creates_an_account_only_where_self_registration_is_open(
        string environment, string? acknowledgement, bool admitted)
    {
        var federated = new FederatedHarness(environment, acknowledgement);

        var resolved = await federated.Service.ResolveOrCreateAsync(federated.Identity, tenantId: null);

        if (admitted)
        {
            resolved.IsError.Should().BeFalse();
            federated.Users.Should().ContainSingle();
        }
        else
        {
            resolved.Code.Should().Be(SelfRegistrationPolicy.DisabledCode);
            federated.Users.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task A_federated_identity_already_bound_signs_in_on_a_closed_node()
    {
        var federated = new FederatedHarness(Environments.Production, acknowledgement: null);
        var existing = new User { Id = Guid.NewGuid(), Username = "oauth-holder", IsActive = true };
        federated.Users.Add(existing);
        federated.Bindings.Add(new UserExternalLogin
        {
            Id = "binding-1",
            TenantScope = KycPersonKeyDeriver.ScopeValue(null),
            Provider = federated.Identity.Provider,
            Subject = federated.Identity.Subject,
            UserId = existing.Id.ToString("N"),
        });

        var resolved = await federated.Service.ResolveOrCreateAsync(federated.Identity, tenantId: null);

        resolved.IsError.Should().BeFalse();
        resolved.Result!.Id.Should().Be(existing.Id);
    }

    private static IConfiguration NodeConfiguration(string? acknowledgement) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "account-creation-policy-signing-key-for-tests!!",
            ["Jwt:Issuer"] = "test",
            ["Jwt:Audience"] = "test",
            [SelfRegistrationPolicy.ProductionAcknowledgementKey] = acknowledgement,
        }).Build();

    private static IHostEnvironment EnvironmentNamed(string name)
        => Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == name);

    /// <summary>Real controller + manager over an in-memory user table; only the stores and signature verifier are faked.</summary>
    private sealed class WalletHarness
    {
        public List<IUser> Users { get; } = [];
        public WalletAuthController Controller { get; }
        public WalletVerifyRequest Request { get; } = new() { Address = Address, ChainType = Chain, Signature = "c2ln" };

        public WalletHarness(string environment, string? acknowledgement, bool signatureValid = true)
        {
            const string nonce = "nonce-qa";
            var expires = DateTime.UtcNow.AddMinutes(4);
            var challenges = new Mock<IWalletAuthChallengeStore>();
            challenges.Setup(store => store.GetLatestLiveByAddressAsync(Address, Chain, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<WalletAuthChallenge>.Success(new WalletAuthChallenge
                {
                    Address = Address, ChainType = Chain, Nonce = nonce, ExpiresAt = expires,
                    DomainMessage = $"AZOA-AUTH-v1\nissuer:test\naudience:test\nchain:{Chain}\naddress:{Address}\nnonce:{nonce}\nexpires:{expires:O}",
                }));
            challenges.Setup(store => store.TryConsumeAsync(nonce, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<bool>.Success(true));

            var verifier = new Mock<IWalletSignatureVerifier>();
            verifier.Setup(item => item.Verify(Chain, Address, It.IsAny<byte[]>(), It.IsAny<byte[]>())).Returns(signatureValid);

            var users = new Mock<IUserStore>();
            users.Setup(store => store.GetByAuthWalletAsync(Address, Chain, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new AZOAResult<IUser> { Result = Users.FirstOrDefault(user => user.AuthWalletAddress == Address) });
            users.Setup(store => store.CreateIfAbsentAsync(It.IsAny<IUser>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IUser user, CancellationToken _) => { Users.Add(user); return AZOAResult<IUser>.Success(user); });

            var manager = new WalletAuthManager(challenges.Object, new Mock<IWalletAuthClaimTokenStore>().Object, users.Object,
                verifier.Object, NodeConfiguration(acknowledgement), EnvironmentNamed(environment));
            Controller = new WalletAuthController(manager) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        }
    }

    /// <summary>Real federated-login service over in-memory user and binding tables.</summary>
    private sealed class FederatedHarness
    {
        public List<IUser> Users { get; } = [];
        public List<UserExternalLogin> Bindings { get; } = [];
        public ExternalLoginService Service { get; }
        public ExternalIdentity Identity { get; } = new(ExternalAuthProvider.GOOGLE, Subject: "google-subject-qa", Email: "qa@example.com", DisplayName: "QA");

        public FederatedHarness(string environment, string? acknowledgement)
        {
            var users = new Mock<IUserStore>();
            users.Setup(store => store.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => Users.FirstOrDefault(user => user.Id == id) is { } found
                    ? AZOAResult<IUser>.Success(found) : AZOAResult<IUser>.Failure("User not found."));
            users.Setup(store => store.CreateIfAbsentAsync(It.IsAny<IUser>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IUser user, CancellationToken _) => { Users.Add(user); return AZOAResult<IUser>.Success(user); });

            var logins = new Mock<IUserExternalLoginStore>();
            logins.Setup(store => store.GetBySubjectAsync(It.IsAny<string>(), It.IsAny<ExternalAuthProvider>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string scope, ExternalAuthProvider provider, string subject, CancellationToken _) =>
                    AZOAResult<UserExternalLogin?>.Success(Bindings.FirstOrDefault(binding =>
                        binding.TenantScope == scope && binding.Provider == provider && binding.Subject == subject)));
            logins.Setup(store => store.CreateAsync(It.IsAny<UserExternalLogin>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((UserExternalLogin binding, CancellationToken _) => { Bindings.Add(binding); return AZOAResult<UserExternalLogin>.Success(binding); });
            logins.Setup(store => store.TouchLastLoginAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<bool>.Success(true));

            Service = new ExternalLoginService(users.Object, logins.Object, NullLogger<ExternalLoginService>.Instance,
                configuration: NodeConfiguration(acknowledgement), environment: EnvironmentNamed(environment));
        }
    }
}
