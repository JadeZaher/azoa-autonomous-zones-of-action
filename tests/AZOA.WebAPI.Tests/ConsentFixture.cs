using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Consent;
using AZOA.WebAPI.Services.Signing;
using Moq;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Tests;

internal static class ConsentFixture
{
    public static SigningConsentWitness Authority(Guid tenant) => new(1,
        new(1, "Jwt", tenant, null, null, DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddHours(1),
            null, null, [], true, TokenUse: AzoaClaims.TokenUseLogin), [AzoaScopes.TenantProvision], []);

    public static ITenantProvisionedGrantIssuer Issuer()
    {
        var issuer = new Mock<ITenantProvisionedGrantIssuer>();
        issuer.Setup(item => item.IssueOnProvisionAsync(It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<SigningConsentWitness>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid user, string external, SigningConsentWitness witness, CancellationToken _) =>
                AZOAResult<ConsentLifecycleReceipt>.Success(new(Guid.NewGuid(), Guid.NewGuid(), user,
                    witness.Credential.ActorUserId, TenantProvisionedGrant.ImpliedScopes.ToArray(), GrantOrigin.Participation,
                    TenantProvisionedGrant.ParticipationRefFor(witness.Credential.ActorUserId, external), DateTimeOffset.UtcNow, null)));
        return issuer.Object;
    }

    public static SigningAdmissionAuthorityFactory Factory(Guid actor)
    {
        var users = new Mock<IUserStore>();
        users.Setup(item => item.GetByIdAsync(actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IUser>.Success(new User { Id = actor, IsActive = true }));
        return new(users.Object, Mock.Of<IWalletStore>(), Mock.Of<ISurrealExecutor>());
    }

    public static Task<AZOAResult<ChildUserResponse>> ProvisionFixtureAsync(this TenantManager manager,
        Guid tenant, ProvisionChildModel model, CancellationToken ct = default)
        => manager.ProvisionChildAsync(tenant, model, Authority(tenant), ct);

    public static Task<AZOAResult<TenantCustodialAccountStatusResponse>> EnsureFixtureAsync(this TenantCustodialAccountManager manager,
        Guid tenant, string external, string key, CancellationToken ct = default)
        => manager.EnsureAsync(tenant, external, key, Authority(tenant) with
            { RequiredScopes = [AzoaScopes.TenantProvision, AzoaScopes.WalletManage, AzoaScopes.KycRead] }, ct);

    public static Task<AZOAResult<TenantCustodialAccountStatusResponse>> GetStatusFixtureAsync(this TenantCustodialAccountManager manager,
        Guid tenant, string external, CancellationToken ct = default)
        => manager.GetStatusAsync(tenant, external, Authority(tenant) with { RequiredScopes = [AzoaScopes.TenantProvision, AzoaScopes.KycRead] }, ct);

    public static Task<AZOAResult<TenantKycSessionResponse>> BeginKycFixtureAsync(this TenantCustodialAccountManager manager,
        Guid tenant, string external, string key, CancellationToken ct = default)
        => manager.BeginKycAsync(tenant, external, key, Authority(tenant) with { RequiredScopes = [AzoaScopes.TenantProvision, AzoaScopes.KycSubmit] }, ct);

    public static Task<AZOAResult<TenantKycSubmissionResponse>> SubmitKycFixtureAsync(this TenantCustodialAccountManager manager,
        Guid tenant, string external, TenantKycSubmissionRequest request, CancellationToken ct = default)
        => manager.SubmitKycAsync(tenant, external, request, Authority(tenant) with { RequiredScopes = [AzoaScopes.TenantProvision, AzoaScopes.KycSubmit] }, ct);

    public static Mock<ITenantCustodialAccess> Access()
    {
        var access = new Mock<ITenantCustodialAccess>();
        access.Setup(item => item.CaptureAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<SigningConsentWitness>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid user, string external,
                SigningConsentWitness authority, string _, CancellationToken _) => new TenantCustodialWitness(authority,
                    user, external, null, "membership", 0, "grant", 0, string.Join(',', authority.RequiredScopes), null, "command", "receipt"));
        access.Setup(item => item.ValidateAsync(It.IsAny<TenantCustodialWitness>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }
}
