
### Atomic lifecycle entry points

ConsentLifecycleService captures the actual credential before user grant/revoke or tenant offboard and delegates the atomic lifecycle store. Expected invalid commands become typed failures; infrastructure errors propagate. ConsentExpiryWorker observes bounded persisted expiry facts even when outbound HTTP delivery is disabled. It does not alter expiry or restore a grant. The old internal provisioning issuer and account-claim sweep still require cutover before the legacy manager mutation surface can be removed.

### Provisioning issuer cutover

TenantProvisionedGrantIssuer now requires the atomic lifecycle store and the actual tenant SigningConsentWitness. It returns the immutable original command receipt, including after later grant revocation. TenantManager has no optional issuer or constructed fallback. The grant transaction validates current tenant credential, exact child ownership/external correlation/auth watermark, approved membership and destination evidence. A create-only identity can survive a failed grant admission; the caller reports failure and retry cannot manufacture a grant after ownership changed. Account claiming still requires its own atomic ownership/revocation notification migration.

### Delegated child credentials

D-5 (round 4). A tenant API key with `tenant:provision` issues a 15-minute child JWT through
`POST /api/tenant/users/{id}/credential`. The issued scopes are the intersection of the tenant
key's scopes, the user's live grant, the request, and `DelegatedChildCredentialCatalog.Scopes`.
That catalog is every scope declared by an endpoint's `[DelegatedChildCredential(scope, signingScopes...)]`
attribute, so a tenant cannot delegate a scope that no endpoint accepts. `tenant:provision` is never
delegated (TenantManager).

`DelegatedChildCredentialGate.AdmitAsync` runs inside `JwtBearerEvents.OnTokenValidated` on every request
that presents a child JWT. It admits the token only when all of these hold: the node switch
`Tenant:DelegatedChildCredentials:Enabled` is on; the endpoint carries the attribute; the token carries
the endpoint scope; the issuing tenant (`act_as_tenant`) is still an active user; the tenant API key that
minted it (`issuing_key_id`) is still active, unrevoked, unexpired and still carries the endpoint scope;
the user is still active; the user's live dApp role grants a role-bound endpoint scope (`dapp:manage`,
`dapp:develop`); and the user still has a live grant to that tenant covering the endpoint scope. On
admission the gate adds an `azoa-live-role` identity holding the store-loaded role, which is the only role
the `DappManage`/`DappDevelop` policies trust for a delegated principal (`Helpers/AGENTS.md` §Role-bound
dApp policies). The generic `AuthNotBefore` watermark check still runs after it. A refusal fails
authentication (401). Consequences:

- *Role-bound scopes (RV-L4-01):* issuance drops `dapp:manage`/`dapp:develop` unless the user's live role
  holds it, and a demotion after issuance kills the credential on its next request. A tenant can never
  make a consenting user a DApp manager.
- *Key revoke or rotation (RV-L4-06):* revoking or rotating the minting tenant key kills its child
  credentials on their next request instead of at their 15-minute expiry.
- *Issuing-tenant revoke:* a tenant offboarding (`DELETE /api/tenant/consent/...`) or a user revoking the
  grant kills every outstanding child JWT on its next request, not at its natural expiry.
- *Acting-tenant reads:* allowlisted endpoints read with `User.GetActingTenantId()`, so rows created under
  one tenant's child credential are invisible to another tenant's (deployments filter by actor and tenant).
- *Signing:* the endpoint scope only opens the door. Value effects still go through
  `SigningAdmissionAuthorityFactory`, which requires the kind-specific signing scope on the token and a
  live `UserExplicit` grant per scope, locked in the effect's transaction.

The switch is off in `appsettings.json` and on only in `appsettings.IntegrationTest.json`. Production is
enabled only after the route-authorization gate and the tenant-branch flows pass the round-4 sweep.
The allowlist must equal the `childCredentialScope`/`childSigningScopes` rows in the lane inventories
(`RouteAuthorizationGateTests`).

### Implied exchange coverage

The tenant-provisioned implied grant also covers `swap:sign` for the W5.1 reserve exchange, computed
at check time from its reserved participation-ref prefix rather than stored. Rule, rationale and the
`transfer:sign` exclusion: `Core/AGENTS.md` §consent-scope-coverage.
