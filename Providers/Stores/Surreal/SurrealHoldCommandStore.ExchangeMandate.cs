using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    private static IReadOnlyList<SurrealQuery>? ComposeExchangeMandate(ExchangeOpeningCommit exchange,
        HoldLocalCommandPlan plan, DateTimeOffset now)
    {
        var consent = exchange.Intent.ReleaseConsent;
        if (consent is null || consent.RequestId == Guid.Empty || exchange.Intent.Opening.Definition?.AutoRelease != true
            || consent.Authority.Credential.Kind != "ApiKey"
            || CanonicalJson.Hash(consent.Authority.Credential) != CanonicalJson.Hash(exchange.Authority.Tenant.Credential)
            || !consent.Authority.RequiredScopes.SequenceEqual([AzoaScopes.TransferSign])
            || consent.DisclosureHash != CanonicalJson.Hash(new { consent.Terms, consent.Quote })
            || !HoldReleaseMandateIdentity.ValidTerms(plan.Result, new(consent.RequestId, plan.Result.Version, consent.Terms, consent.Quote))) return null;
        var guards = SigningAuthorityFence.ForConsent(consent.Authority, [AzoaScopes.TransferSign], "exchange_release_accept");
        if (guards is null) return null;
        var mandateId = CanonicalJson.Hash(new { Domain = "exchange-release-mandate-v1", exchange.Source.Id });
        var view = JsonSerializer.Deserialize<HoldCommandView>(plan.Receipt.OutcomeJson)!;
        plan.Receipt.OutcomeJson = JsonSerializer.Serialize(view with { ReleaseMandateId = mandateId, MandateTerms = consent.Terms });
        var source = new HoldReleaseExchangeSource(1, exchange.Source.Id, exchange.Source.IntentHash, plan.Receipt.Id,
            CanonicalJson.HashCanonical(plan.Receipt.OutcomeJson), exchange.Authority);
        var authorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(consent.Authority));
        var auditId = CanonicalJson.Hash(new { Domain = "exchange-release-acceptance-v1", exchange.Source.Id, consent.RequestId });
        var mandate = new HoldReleaseMandate { Id = mandateId, ActorUserId = exchange.Source.TenantUserId,
            HoldRecordId = plan.Result.Id, RequestHash = consent.DisclosureHash,
            TermsJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(consent.Terms)), TermsHash = CanonicalJson.Hash(consent.Terms),
            OriginalAuthorityJson = authorityJson, CurrentAuthorityJson = authorityJson, CurrentAuthorizationId = auditId,
            OriginalQuoteJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(consent.Quote)),
            ExchangeSourceJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(source)),
            NotBefore = consent.Terms.NotBefore, ExpiresAt = consent.Terms.ExpiresAt, NextAttemptAt = consent.Terms.NotBefore,
            CreatedAt = now, UpdatedAt = now };
        plan.Result.ActiveReleaseMandateId = mandateId;
        var audit = new HoldRecoveryReceipt { Id = auditId, OperationId = plan.Receipt.Id, Kind = "ExchangeReleaseAccept",
            ActorUserId = mandate.ActorUserId, RequestHash = consent.DisclosureHash, AuthorityJson = authorityJson,
            OriginalState = exchange.Source.State, OriginalVersion = exchange.Source.Version,
            OutcomeJson = plan.Receipt.OutcomeJson, CreatedAt = now };
        var statements = guards.ToList();
        var recipient = consent.Terms.Recipient;
        var owner = SurrealLink.ToLink("user", recipient.OwnerUserId.ToString("N"))!;
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Wallet>(recipient.Id.ToString("N"))
            .Where(row => row.UserId == owner && row.Address == recipient.Address && row.ChainType == recipient.ChainType)
            .Set(row => row.Address, recipient.Address).Build(), "exchange_release_recipient", "_exchange_release_recipient"));
        // raw: exact release consent, recipient, database-time cost expiry and mandate join the source opening transaction.
        statements.Add(SurrealQuery.Of("""
            IF $_exchange_release_recipient = NONE OR $_exchange_release_recipient = []
                OR $_exchange_release_quote_expiry <= time::now() OR $_exchange_release_expiry <= time::now()
                { THROW 'HOLD_COMMAND_CHANGED' }
            """).WithParam("_exchange_release_quote_expiry", consent.Quote.ExpiresAt)
            .WithParam("_exchange_release_expiry", consent.Terms.ExpiresAt));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(mandate), "exchange_release_mandate"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(audit), "exchange_release_audit"));
        return statements;
    }
}
