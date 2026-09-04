// SPDX-License-Identifier: UNLICENSED
//
// D17 rule 2: OAuth proves control of an ACCOUNT; Identomat proves a PERSON.
// Conflating them on the value gate would let anyone with a Google account move value.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Tests.Auth;

public class OAuthDoesNotSatisfyKycTests
{
    /// <summary>
    /// The behavioural proof. An avatar created by federated sign-in has no
    /// kyc_submission, because nothing on the OAuth path writes one. The real gate,
    /// asked about that avatar, refuses.
    /// </summary>
    [Fact]
    public async Task An_oauth_only_avatar_does_not_pass_the_value_gate()
    {
        var oauthAvatarId = Guid.NewGuid();

        var kycStore = new Mock<IKycStore>();
        // The true state of an OAuth-only avatar: no identity verification on record.
        kycStore
            .Setup(store => store.GetLatestSubmissionByAvatarAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });
        kycStore
            .Setup(store => store.GetLatestSubmissionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });

        var gate = new KycGateService(
            kycStore.Object,
            Mock.Of<IKycScreeningStore>(),
            Mock.Of<IKycProviderService>(),
            Options.Create(new KycSettings()),
            EnvironmentNamed("Production"));

        var valueAccess = new ValueAccessService(gate);

        var decision = await valueAccess.GetDecisionAsync(oauthAvatarId);
        decision.State.Should().Be(ValueAccessState.VerificationRequired,
            "a federated login proves account control, never that a person was verified");

        var required = await valueAccess.RequireValueAccessAsync(oauthAvatarId);
        required.IsError.Should().BeTrue();
        required.Result.Should().BeFalse();
    }

    /// <summary>
    /// The structural proof, which survives future edits the behavioural one would not:
    /// the KYC decision path has no reference to the federated-login table or its store,
    /// so no amount of OAuth activity can ever become evidence of identity.
    /// </summary>
    [Fact]
    public void The_kyc_gate_has_no_reference_to_the_federated_login_surface()
    {
        var assembly = typeof(KycGateService).Assembly;

        var kycDecisionTypes = new[]
        {
            typeof(KycGateService),
            typeof(ValueAccessService),
            typeof(KycApprovalTrust),
        };

        foreach (var type in kycDecisionTypes)
        {
            var referenced = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType))
                .Concat(type.GetConstructors()
                    .SelectMany(constructor => constructor.GetParameters())
                    .Select(parameter => parameter.ParameterType))
                .Concat(type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(field => field.FieldType))
                .ToList();

            referenced.Should().NotContain(typeof(IAvatarExternalLoginStore),
                $"{type.Name} must not be able to read federated logins");
            referenced.Should().NotContain(typeof(AvatarExternalLogin),
                $"{type.Name} must not treat a federated binding as identity evidence");
        }

        // And nothing in the federated-login surface writes KYC evidence.
        var externalLoginService = assembly.GetType(
            "AZOA.WebAPI.Services.Auth.ExternalLoginService")!;
        var externalDependencies = externalLoginService.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

        externalDependencies.Should().NotContain(typeof(IKycStore));
        externalDependencies.Should().NotContain(typeof(IKycGateService));
        externalDependencies.Should().NotContain(typeof(IValueAccessService));
        externalDependencies.Should().NotContain(typeof(IKycIdentityStore));
    }

    /// <summary>
    /// An avatar minted by federated sign-in starts unverified. IsVerified is the KYC
    /// flag; a provider asserting a verified EMAIL must never set it.
    /// </summary>
    [Fact]
    public async Task A_federated_sign_in_never_marks_the_avatar_verified()
    {
        var created = new List<AZOA.WebAPI.Interfaces.IAvatar>();

        var avatarStore = new Mock<IAvatarStore>();
        avatarStore
            .Setup(store => store.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<AZOA.WebAPI.Interfaces.IAvatar>.Failure("Avatar not found."));
        avatarStore
            .Setup(store => store.CreateIfAbsentAsync(
                It.IsAny<AZOA.WebAPI.Interfaces.IAvatar>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AZOA.WebAPI.Interfaces.IAvatar avatar, CancellationToken _) =>
            {
                created.Add(avatar);
                return AZOAResult<AZOA.WebAPI.Interfaces.IAvatar>.Success(avatar);
            });

        var loginStore = new Mock<IAvatarExternalLoginStore>();
        loginStore
            .Setup(store => store.GetBySubjectAsync(
                It.IsAny<string>(), It.IsAny<AZOA.WebAPI.Models.Auth.ExternalAuthProvider>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<AvatarExternalLogin?>.Success(null));
        loginStore
            .Setup(store => store.CreateAsync(
                It.IsAny<AvatarExternalLogin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AvatarExternalLogin binding, CancellationToken _) =>
                AZOAResult<AvatarExternalLogin>.Success(binding));

        var service = new AZOA.WebAPI.Services.Auth.ExternalLoginService(
            avatarStore.Object,
            loginStore.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                AZOA.WebAPI.Services.Auth.ExternalLoginService>.Instance);

        await service.ResolveOrCreateAsync(
            new AZOA.WebAPI.Services.Auth.ExternalIdentity(
                AZOA.WebAPI.Models.Auth.ExternalAuthProvider.GOOGLE,
                "verified-google-subject",
                Email: "verified@gmail.com"),
            tenantId: null);

        created.Should().ContainSingle();
        created[0].IsVerified.Should().BeFalse(
            "a provider-verified EMAIL is not a verified PERSON");
    }

    private static IHostEnvironment EnvironmentNamed(string environmentName)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(host => host.EnvironmentName).Returns(environmentName);
        return environment.Object;
    }
}
