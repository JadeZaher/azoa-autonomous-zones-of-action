
### Atomic lifecycle entry points

ConsentLifecycleService captures the actual credential before user grant/revoke or tenant offboard and delegates the atomic lifecycle store. Expected invalid commands become typed failures; infrastructure errors propagate. ConsentExpiryWorker observes bounded persisted expiry facts even when outbound HTTP delivery is disabled. It does not alter expiry or restore a grant. The old internal provisioning issuer and account-claim sweep still require cutover before the legacy manager mutation surface can be removed.

### Provisioning issuer cutover

TenantProvisionedGrantIssuer now requires the atomic lifecycle store and the actual tenant SigningConsentWitness. It returns the immutable original command receipt, including after later grant revocation. TenantManager has no optional issuer or constructed fallback. The grant transaction validates current tenant credential, exact child ownership/external correlation/auth watermark, approved membership and destination evidence. A create-only identity can survive a failed grant admission; the caller reports failure and retry cannot manufacture a grant after ownership changed. Account claiming still requires its own atomic ownership/revocation notification migration.
