// SPDX-License-Identifier: UNLICENSED

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>
/// Records the person-to-user binding produced by a verified identity and
/// answers eligibility questions about it. Every consumer-visible answer is
/// deliberately coarse; see <c>Services/Kyc/AGENTS.md</c> §identity-binding.
/// </summary>
public interface IKycIdentityBindingService
{
    /// <summary>
    /// Derives the scope-salted person key and binds the user to it. Fails closed
    /// when the derivation inputs or the root secret are unusable — an unbound
    /// verification is recoverable, a wrongly-bound one is not.
    /// </summary>
    Task<AZOAResult<KycIdentity>> BindAsync(
        Guid userId,
        Guid? tenantId,
        KycProvider provider,
        string providerKey,
        HostedSessionSubjectAttributes attributes,
        CancellationToken ct = default);

    /// <summary>
    /// Decides whether this user may claim a person-scoped benefit. The result is
    /// intentionally two-valued at the consumer boundary.
    /// </summary>
    Task<AZOAResult<KycEligibilityDecision>> EvaluateEligibilityAsync(
        Guid userId,
        Guid? tenantId,
        string personKey,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class KycIdentityBindingService : IKycIdentityBindingService
{
    /// <summary>
    /// The SINGLE denial message a consumer may ever see. "Already claimed by
    /// another user" and "not verified" must be indistinguishable: the former
    /// would tell a caller that their subject exists somewhere else on the node.
    /// </summary>
    public const string IneligibleMessage = "This user is not eligible for this operation.";

    private const string UnavailableMessage = "Identity binding is unavailable.";
    private const int ErasureBatchLimit = 200;

    private readonly IKycIdentityStore _store;
    private readonly IKycPersonKeyDeriver _deriver;
    private readonly KycIdentitySettings _settings;
    private readonly ILogger<KycIdentityBindingService> _logger;
    private readonly TimeProvider _clock;

    public KycIdentityBindingService(
        IKycIdentityStore store,
        IKycPersonKeyDeriver deriver,
        IOptions<KycSettings> options,
        ILogger<KycIdentityBindingService>? logger = null,
        TimeProvider? clock = null)
    {
        _store = store;
        _deriver = deriver;
        _settings = options.Value.Identity;
        _logger = logger ?? NullLogger<KycIdentityBindingService>.Instance;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AZOAResult<KycIdentity>> BindAsync(
        Guid userId,
        Guid? tenantId,
        KycProvider provider,
        string providerKey,
        HostedSessionSubjectAttributes attributes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (userId == Guid.Empty)
            return AZOAResult<KycIdentity>.Failure("An user is required to bind an identity.");
        if (!_deriver.IsConfigured)
        {
            _logger.LogError(
                "KYC identity binding is unconfigured; set {KeyName} before enabling a live provider.",
                KycIdentitySettings.PersonKeySecretName);
            return AZOAResult<KycIdentity>.FailureWithCode(
                UnavailableMessage,
                AzoaErrorCodes.DependencyUnavailable);
        }

        var scope = _deriver.ScopeFor(tenantId);
        var personKey = _deriver.DerivePersonKey(
            scope,
            attributes.DocumentNumber,
            attributes.IssuingState);
        var globalPersonKey = _deriver.DerivePersonKey(
            _deriver.GlobalScope,
            attributes.DocumentNumber,
            attributes.IssuingState);
        if (personKey is null || globalPersonKey is null)
        {
            _logger.LogWarning(
                "A verified identity carried no usable document identity; refusing to bind user {UserId}.",
                userId);
            return AZOAResult<KycIdentity>.Failure(
                "The verified identity did not carry a usable document identity.");
        }

        var now = _clock.GetUtcNow();
        var candidate = new KycIdentity
        {
            Id = KycPersonKeyDeriver.RecordId(scope, personKey),
            TenantScope = scope,
            TenantId = tenantId is null || tenantId == Guid.Empty ? null : tenantId.Value.ToString("N"),
            PersonKey = personKey,
            GlobalPersonKey = globalPersonKey,
            PersonKeyVersion = _deriver.RecipeVersion,
            UserIds = [userId.ToString("N")],
            FirstName = Trim(attributes.FirstName),
            LastName = Trim(attributes.LastName),
            Birthday = Trim(attributes.Birthday),
            Citizenship = Trim(attributes.Citizenship),
            Provider = provider,
            ProviderKey = providerKey,
            RetentionExpiresAt = now.AddDays(Math.Clamp(_settings.RetentionDays, 1, 3650)),
            CreatedDate = now,
            ModifiedDate = now,
        };

        var bound = await _store.BindUserAsync(candidate, ct);
        if (bound.IsError || bound.Result is null)
            return bound;

        // The store's append is conditional, so a person key already held by ANOTHER
        // user in this scope comes back without the caller in it. That is the D3/D23
        // Sybil refusal, and it is detected by reading the result rather than by
        // trusting the write — an unconditional append would make it undetectable.
        var userKey = userId.ToString("N");
        if (!(bound.Result.UserIds ?? []).Contains(userKey, StringComparer.Ordinal))
        {
            _logger.LogWarning(
                "Refusing to bind user {UserId}: this person is already bound to a different user in the same scope ({Cause}).",
                userId,
                KycIneligibilityCause.PersonAlreadyBoundToAnotherUser);
            // The consumer-visible sentence is the SAME one every other refusal uses.
            // "Already claimed by another user" would tell a tenant its subject
            // exists elsewhere, which is exactly the fact per-tenant salting hides.
            return AZOAResult<KycIdentity>.FailureWithCode(
                IneligibleMessage,
                AzoaErrorCodes.OperationNotAllowed);
        }

        return bound;
    }

    public async Task<AZOAResult<KycEligibilityDecision>> EvaluateEligibilityAsync(
        Guid userId,
        Guid? tenantId,
        string personKey,
        CancellationToken ct = default)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(personKey))
            return Ineligible(KycIneligibilityCause.NoVerifiedIdentity);

        var scope = _deriver.ScopeFor(tenantId);
        var existing = await _store.GetByPersonKeyAsync(scope, personKey, ct);
        if (existing.IsError)
        {
            return AZOAResult<KycEligibilityDecision>.FailureWithCode(
                UnavailableMessage,
                AzoaErrorCodes.DependencyUnavailable);
        }

        if (existing.Result is null)
            return Ineligible(KycIneligibilityCause.NoVerifiedIdentity);

        var userKey = userId.ToString("N");
        var boundUsers = existing.Result.UserIds ?? [];
        if (boundUsers.Contains(userKey, StringComparer.Ordinal))
            return AZOAResult<KycEligibilityDecision>.Success(KycEligibilityDecision.Eligible());

        // A different user already holds this person key inside this scope. The
        // operator-side cause records why; the consumer-side message does not.
        return Ineligible(KycIneligibilityCause.PersonAlreadyBoundToAnotherUser);
    }

    /// <summary>Batch size the erasure sweep may claim in one pass.</summary>
    public static int ErasureBatchSize => ErasureBatchLimit;

    private static AZOAResult<KycEligibilityDecision> Ineligible(KycIneligibilityCause cause)
        => AZOAResult<KycEligibilityDecision>.Success(KycEligibilityDecision.Ineligible(cause));

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
