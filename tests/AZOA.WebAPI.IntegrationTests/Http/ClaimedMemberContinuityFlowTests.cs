using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Auth;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Audit = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentAudit;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentWebhookEvent;
using Grant = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;
using Wallet = AZOA.WebAPI.Persistence.SurrealDb.Models.Wallet;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>
/// N2 claimed-member continuity: a member who claims their account keeps working with the tenant under the
/// UserExplicit grant they give it, and the tenant sees only the minimal status until that grant covers kyc:read.
/// See <c>Http/AGENTS.md</c> §claimed-continuity.
/// </summary>
public sealed class ClaimedMemberContinuityFlowTests(ClaimedContinuityWebApplicationFactory factory)
    : HttpFlowTestBase(factory), IClassFixture<ClaimedContinuityWebApplicationFactory>
{
    private const string KycCallback = "api/kyc/callback/identomat";
    private static readonly string[] PersonScopes =
        [AzoaScopes.KycRead, AzoaScopes.UnitsIssue, AzoaScopes.SwapSign, AzoaScopes.TransferSign, AzoaScopes.KycSubmit];
    private static readonly string[] MinimalStatusFields = ["ownership", "consent", "kycStatus", "ready"];

    [Fact]
    public async Task A_claimed_member_keeps_working_with_the_tenant_under_their_own_grant_until_either_side_ends_it()
    {
        using var tenant = await OnboardValueTenantAsync();
        var member = await ProvisionApprovedMemberAsync(tenant);

        // Relinquish is for claimed members only: on a custodial one it would strand the open membership.
        using (var custodialRelinquish = await tenant.Custody.DeleteAsync(AccountRoute(member.Subject) + "/consent"))
        {
            await ShouldReturnAsync(custodialRelinquish, HttpStatusCode.Conflict);
            JsonSerializer.Deserialize<AZOAResult<ConsentRelinquishReceipt>>(await custodialRelinquish.Content.ReadAsStringAsync(), JsonOptions)!
                .Code.Should().Be(ConsentRelinquishErrors.MemberStillCustodialCode);
        }
        var stillCustodial = await StatusAsync(tenant, member.Subject);
        stillCustodial.Ownership.Should().Be(TenantMemberOwnership.Custodial);
        stillCustodial.Ready.Should().BeTrue(stillCustodial.UnavailableReason);

        using var person = await ClaimAsync(tenant, member);
        var grant = await GrantAsync(person, tenant.TenantId);

        var status = await StatusAsync(tenant, member.Subject);
        status.Ownership.Should().Be(TenantMemberOwnership.Claimed);
        status.Consent.Should().Be(TenantMemberConsent.Granted);
        status.ConsentScopes.Should().BeEquivalentTo(PersonScopes);
        status.KycStatus.Should().Be(TenantKycStatus.Approved);
        status.Ready.Should().BeTrue(status.UnavailableReason);
        status.WalletId.Should().Be(member.WalletId);

        var ensured = await ReadOkAsync<TenantCustodialAccountStatusResponse>(await EnsureAsync(tenant, member.Subject, "after-claim-" + member.Subject));
        ensured.WalletId.Should().Be(member.WalletId, "ensure never re-provisions or re-bootstraps a claimed member");
        ensured.Ownership.Should().Be(TenantMemberOwnership.Claimed);
        ensured.Ready.Should().BeTrue(ensured.UnavailableReason);
        (await WalletsOfAsync(member.UserId)).Should().ContainSingle();

        var checkout = await ReadOkAsync<FiatCheckoutIntent>(await BeginCheckoutAsync(tenant, member.UserId, CheckoutKey));
        var openIntent = await ReadOkAsync<FiatCheckoutIntent>(await tenant.Custody.GetAsync(IntentRoute(member.UserId, checkout.IntentId)));
        await ShouldBeAdmittedExchangeAsync(await ExchangePreviewAsync(tenant, member.UserId, "exchange-with-grant"), tenant, member.UserId);
        await ReadOkAsync<JsonElement>(await tenant.Custody.GetAsync($"api/fiat/payouts/{member.UserId}/availability"));
        var refund = await ReadOkAsync<AllocationResult>(await RefundPreviewAsync(tenant, member.UserId));
        refund.OperationId.Should().Be(Guid.Empty, "a preview admits every gate and moves nothing");
        refund.WalletId.Should().Be(Guid.Parse(member.WalletId));

        using (var revoked = await person.DeleteAsync($"api/user/consent/{grant.GrantId}"))
            await ShouldReturnAsync(revoked, HttpStatusCode.OK);
        await ShouldBeMinimalStatusAsync(await tenant.Custody.GetAsync(AccountRoute(member.Subject)));
        await ShouldReturnAsync(await BeginCheckoutAsync(tenant, member.UserId, "checkout-after-revoke"), HttpStatusCode.Forbidden);
        await ShouldReturnAsync(await tenant.Custody.GetAsync(IntentRoute(member.UserId, checkout.IntentId)), HttpStatusCode.Forbidden);
        await ShouldReturnAsync(await tenant.Custody.PostAsync(VoidRoute(member.UserId, CheckoutKey), null), HttpStatusCode.Forbidden);
        await ShouldReturnAsync(await ExchangePreviewAsync(tenant, member.UserId, "exchange-after-revoke"), HttpStatusCode.Forbidden);
        await ShouldReturnAsync(await RefundPreviewAsync(tenant, member.UserId), HttpStatusCode.Forbidden);

        var regranted = await GrantAsync(person, tenant.TenantId);
        (await StatusAsync(tenant, member.Subject)).Ready.Should().BeTrue();
        var intentAfter = await ReadOkAsync<FiatCheckoutIntent>(await tenant.Custody.GetAsync(IntentRoute(member.UserId, checkout.IntentId)));
        intentAfter.Status.Should().Be(openIntent.Status, "the refused void abandoned nothing on the tenant's behalf");
        intentAfter.Status.Should().NotBe(FiatIntentStatuses.Expired);
        using var relinquished = await tenant.Custody.DeleteAsync(AccountRoute(member.Subject) + "/consent");
        var receipt = await ReadOkAsync<ConsentRelinquishReceipt>(relinquished);
        receipt.RevokedGrantIds.Should().Equal(regranted.GrantId);
        receipt.ExternalSubject.Should().Be(member.Subject);

        var memberLink = SurrealLink.ToLink("user", member.UserId.ToString("N"));
        (await RowsAsync(SurrealQuery<Grant>.From().Where(row => row.GrantorUserId == memberLink)))
            .Should().OnlyContain(row => row.RevokedAt != null, "the tenant gave up every grant, whatever its origin");
        var grantLink = SurrealLink.ToLink("consent_grant", regranted.GrantId.ToString("N"));
        (await RowsAsync(SurrealQuery<Event>.From().Where(row => row.GrantId == grantLink)))
            .Should().ContainSingle(row => row.EventType == Event.WebhookEventType.Revoked);
        var grantId = regranted.GrantId.ToString("N");
        (await RowsAsync(SurrealQuery<Audit>.From().Where(row => row.GrantId == grantId)))
            .Should().ContainSingle(row => row.Action == "Revoked").Which.Detail.Should().Be("relinquished by tenant");
        await ShouldBeMinimalStatusAsync(await tenant.Custody.GetAsync(AccountRoute(member.Subject)));
    }

    [Fact]
    public async Task Without_a_grant_a_claimed_member_is_four_fields_to_its_tenant_and_nothing_to_any_other()
    {
        using var tenant = await OnboardValueTenantAsync();
        var member = await ProvisionApprovedMemberAsync(tenant);
        using var person = await ClaimAsync(tenant, member);

        await ShouldBeMinimalStatusAsync(await tenant.Custody.GetAsync(AccountRoute(member.Subject)));
        await ShouldBeMinimalStatusAsync(await EnsureAsync(tenant, member.Subject, "after-claim-" + member.Subject));
        (await WalletsOfAsync(member.UserId)).Should().ContainSingle();

        using var stranger = await OnboardTenantAsync();
        await TestDataBuilders.KybApprovedTenant(stranger.TenantId).SeedAsync(Factory.Services);
        await ShouldReturnAsync(await stranger.Client.GetAsync(AccountRoute(member.Subject)), HttpStatusCode.NotFound);
        await ShouldReturnAsync(await stranger.Client.DeleteAsync(AccountRoute(member.Subject) + "/consent"), HttpStatusCode.NotFound);

        // A grant without kyc:read: consent is truthfully "granted", yet KYC and readiness stay hidden.
        await GrantAsync(person, tenant.TenantId, [AzoaScopes.UnitsIssue, AzoaScopes.TransferSign]);
        using var partial = await tenant.Custody.GetAsync(AccountRoute(member.Subject));
        await ShouldReturnAsync(partial, HttpStatusCode.OK);
        var result = await ResultElementAsync(partial);
        result.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(MinimalStatusFields.Append("consentScopes"));
        result.GetProperty("consent").GetString().Should().Be(TenantMemberConsent.Granted);
        result.GetProperty("consentScopes").EnumerateArray().Select(scope => scope.GetString())
            .Should().BeEquivalentTo(new[] { AzoaScopes.UnitsIssue, AzoaScopes.TransferSign });
        result.GetProperty("kycStatus").GetString().Should().Be(nameof(TenantKycStatus.Unknown));
        result.GetProperty("ready").GetBoolean().Should().BeFalse();
    }

    // ── Actors and onboarding, through public routes only ──────────────────────

    /// <summary>The tenant's operator-issued custody key plus a self-issued value key, as the ArdaNova BFF holds them.</summary>
    private sealed record ValueTenant(Guid TenantId, HttpClient Custody, HttpClient Value) : IDisposable
    {
        public void Dispose()
        {
            Custody.Dispose();
            Value.Dispose();
        }
    }

    private sealed record Member(string Subject, Guid UserId, string WalletId);

    private async Task<ValueTenant> OnboardValueTenantAsync()
    {
        var tenant = await OnboardTenantAsync();
        await AssignIdentomatToTenantAsync(tenant.TenantId);
        await TestDataBuilders.KybApprovedTenant(tenant.TenantId).SeedAsync(Factory.Services);
        using var owner = Factory.CreateAuthenticatedClientForUser(tenant.TenantId);
        using var issued = await owner.PostAsJsonAsync("api/apikey", new CreateApiKeyRequest
        {
            Name = "flow-test-value",
            ExpiresInDays = 30,
            Scopes = string.Join(',', AzoaScopes.SwapSign, AzoaScopes.NftMint, AzoaScopes.UnitsIssue, AzoaScopes.TransferSign),
        }, JsonOptions);
        var key = await ReadOkAsync<CreateApiKeyResponse>(issued);
        return new ValueTenant(tenant.TenantId, tenant.Client, Factory.CreateApiKeyClient(key.Key));
    }

    /// <summary>Ensure → hosted KYC session → vendor approves → callback; the house identity path.</summary>
    private async Task<Member> ProvisionApprovedMemberAsync(ValueTenant tenant)
    {
        var subject = "member-" + Guid.NewGuid().ToString("N")[..12];
        var account = await ReadOkAsync<TenantCustodialAccountStatusResponse>(await EnsureAsync(tenant, subject, "enrol-" + subject));
        account.WalletReady.Should().BeTrue(account.UnavailableReason);
        var userId = Guid.Parse(account.UserId!);

        var begin = new HttpRequestMessage(HttpMethod.Post, AccountRoute(subject) + "/kyc/session");
        begin.Headers.Add("Idempotency-Key", "kyc-session-" + subject);
        await ReadOkAsync<TenantKycSessionResponse>(await tenant.Custody.SendAsync(begin));
        var session = Flow.Identomat.LatestIdentitySessionFor(userId)!;
        session.Verdict = IdentomatVerdict.Approved;
        await PostCallbackAsync(KycCallback, IdentomatVendor.CallbackBody(Flow.CallbackApiKey, session.Id, "APPROVED"));

        var approved = await StatusAsync(tenant, subject);
        approved.Ownership.Should().Be(TenantMemberOwnership.Custodial);
        approved.Ready.Should().BeTrue(approved.UnavailableReason);
        return new Member(subject, userId, account.WalletId!);
    }

    /// <summary>Tenant invites, the person claims with their own password; returns a client holding the login token the claim issued.</summary>
    private async Task<HttpClient> ClaimAsync(ValueTenant tenant, Member member)
    {
        // The claim CAS refuses until the wallet-identity migration worker has swept; run its sweep now.
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WalletAuthIdentityMigrationService>().SweepAsync(CancellationToken.None);
        using var invite = await tenant.Custody.PostAsJsonAsync("api/user/claim-invite",
            new CreateClaimInviteRequest { ChildUserId = member.UserId }, JsonOptions);
        var token = (await ReadOkAsync<ClaimInviteResponse>(invite)).ClaimToken;
        token.Should().NotBeNullOrEmpty("this host stands in for the member's inbox (ReturnTokenToTenant=true)");
        using var claimed = await Anonymous.PostAsJsonAsync("api/user/claim", new ClaimUserRequest
        {
            TargetUserId = member.UserId,
            ClaimToken = token,
            NewPassword = "claimed-" + Guid.NewGuid().ToString("N"),
        }, JsonOptions);
        var login = await ReadOkAsync<WalletAuthTokenResponse>(claimed);
        login.UserId.Should().Be(member.UserId);
        var person = Factory.CreateClient();
        person.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return person;
    }

    private async Task<ConsentLifecycleReceipt> GrantAsync(HttpClient person, Guid tenantId, IReadOnlyList<string>? scopes = null)
    {
        using var response = await person.PostAsJsonAsync("api/user/consent", new GrantConsentRequest
        {
            RequestId = Guid.NewGuid(),
            TenantId = tenantId,
            Scopes = [.. scopes ?? PersonScopes],
            Origin = "UserExplicit",
        }, JsonOptions);
        return await ReadOkAsync<ConsentLifecycleReceipt>(response);
    }

    // ── Tenant calls ───────────────────────────────────────────────────────────

    private const string CheckoutKey = "checkout-with-grant";

    private static string AccountRoute(string subject) => $"api/tenant/custodial-accounts/{subject}";

    private static string IntentRoute(Guid userId, Guid intentId) => $"api/fiat/checkout/{userId}/{intentId}";

    private static string VoidRoute(Guid userId, string requestKey) => $"api/fiat/checkout/{userId}/requests/{requestKey}/void";

    private Task<HttpResponseMessage> EnsureAsync(ValueTenant tenant, string subject, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, AccountRoute(subject));
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return tenant.Custody.SendAsync(request);
    }

    private async Task<TenantCustodialAccountStatusResponse> StatusAsync(ValueTenant tenant, string subject)
        => await ReadOkAsync<TenantCustodialAccountStatusResponse>(await tenant.Custody.GetAsync(AccountRoute(subject)));

    private Task<HttpResponseMessage> BeginCheckoutAsync(ValueTenant tenant, Guid userId, string requestKey)
        => tenant.Custody.PostAsJsonAsync($"api/fiat/checkout/{userId}",
            new FiatCheckoutStartRequest { RequestKey = requestKey, AmountMinorUnits = 1_000 }, JsonOptions);

    private Task<HttpResponseMessage> ExchangePreviewAsync(ValueTenant tenant, Guid userId, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/exchange/{userId}")
        {
            Content = JsonContent.Create(new ExchangeRequest
            {
                ChainType = "Algorand",
                Direction = ExchangeDirection.Buy,
                ProjectReference = "project-flow",
                ProjectAssetId = "1001",
                PlatformAmount = "100",
                ProjectUnitAmount = "10",
                ReservePartition = "sponsorship",
                PreviewOnly = true,
            }, options: JsonOptions),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return tenant.Value.SendAsync(request);
    }

    /// <summary>The BFF's escrow refund: a Transfer allocation back to the member, previewed so nothing moves.</summary>
    private Task<HttpResponseMessage> RefundPreviewAsync(ValueTenant tenant, Guid userId)
        => tenant.Value.PostAsJsonAsync($"api/allocation/{userId}", new AllocationRequest
        {
            Kind = AllocationKind.Transfer,
            ChainType = "Algorand",
            Amount = "10",
            Name = "ArdaNova escrow refund",
            AssetRecordId = Guid.NewGuid(),
            Memo = "refund:escrow-flow",
            PreviewOnly = true,
        }, JsonOptions);

    // ── Assertions ─────────────────────────────────────────────────────────────

    /// <summary>The four fields, and nothing that could carry KYC detail, a wallet or an identity.</summary>
    private async Task ShouldBeMinimalStatusAsync(HttpResponseMessage response)
    {
        using var _ = response;
        await ShouldReturnAsync(response, HttpStatusCode.OK);
        var result = await ResultElementAsync(response);
        result.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(MinimalStatusFields);
        result.GetProperty("ownership").GetString().Should().Be(TenantMemberOwnership.Claimed);
        result.GetProperty("consent").GetString().Should().Be(TenantMemberConsent.None);
        result.GetProperty("kycStatus").GetString().Should().Be(nameof(TenantKycStatus.Unknown));
        result.GetProperty("ready").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// The exchange edge admitted the person's swap:sign grant: the consent gate recorded an allow for this member, and
    /// the only refusal is the node's own state further in (this host has run no platform-unit genesis), never consent.
    /// </summary>
    private async Task ShouldBeAdmittedExchangeAsync(HttpResponseMessage response, ValueTenant tenant, Guid userId)
    {
        using var _ = response;
        var body = await response.Content.ReadAsStringAsync();
        await ShouldReturnAsync(response, HttpStatusCode.ServiceUnavailable);
        JsonSerializer.Deserialize<AZOAResult<ExchangeReceipt>>(body, JsonOptions)!.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable, body);
        var memberLink = SurrealLink.ToLink("user", userId.ToString("N"));
        var tenantLink = SurrealLink.ToLink("user", tenant.TenantId.ToString("N"));
        (await RowsAsync(SurrealQuery<Audit>.From().Where(row => row.UserId == memberLink && row.TenantId == tenantLink)))
            .Should().Contain(row => row.Action == "TenantSignAllowed" && row.Scope == AzoaScopes.SwapSign, body);
    }

    private async Task<IReadOnlyList<Wallet>> WalletsOfAsync(Guid userId)
    {
        var owner = SurrealLink.ToLink("user", userId.ToString("N"));
        return await RowsAsync(SurrealQuery<Wallet>.From().Where(row => row.UserId == owner));
    }
}
