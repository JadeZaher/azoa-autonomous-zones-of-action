using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Versioned tenant registration persistence; infrastructure failures propagate.</summary>
public interface IWebhookRegistrationStore
{
    /// <summary>Atomically apply an exact authorized management command and receipt; only its first commit returns a new secret.</summary>
    Task<AZOAResult<WebhookRegistrationMutationResult>> MutateAsync(Guid tenantId, WebhookRegistrationCommand command,
        SigningConsentWitness authority, CancellationToken ct = default);

    /// <summary>Serialize exact-command withdrawal against application; return its authoritative disposition without secrets.</summary>
    Task<AZOAResult<WebhookRegistrationWithdrawal>> WithdrawAsync(Guid tenantId, WebhookRegistrationCommand command,
        SigningConsentWitness authority, CancellationToken ct = default);

    /// <summary>Create at version zero or update the exact existing identity/version; encrypt any supplied secret.</summary>
    Task<AZOAResult<WebhookRegistration>> UpsertAsync(WebhookRegistration registration, CancellationToken ct = default);

    /// <summary>Read protected registration metadata without decrypting or returning plaintext secrets.</summary>
    Task<AZOAResult<WebhookRegistration>> GetByTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Rotate a secret against the currently read version; management routes require a separate audited expected-version command.</summary>
    Task<AZOAResult<bool>> RotateSecretAsync(Guid tenantId, string newSecret, DateTime now, CancellationToken ct = default);
}
