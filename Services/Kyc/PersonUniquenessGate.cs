// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Kyc;

/// <inheritdoc />
/// <remarks>See <c>Services/Kyc/AGENTS.md</c> §person-uniqueness.</remarks>
public sealed class PersonUniquenessGate : IPersonUniquenessGate
{
    /// <summary>
    /// Prefix of the stand-in key used when a person cannot be resolved. Real person
    /// keys are unpadded base64url of an HMAC, so a '!' can never appear in one and
    /// the sentinel cannot collide with a real human's claim.
    /// </summary>
    public const string SentinelPersonKeyPrefix = "!unresolved!";

    private readonly IKycIdentityStore _identities;
    private readonly IPersonOperationClaimStore _claims;
    private readonly IKycPersonKeyDeriver _deriver;
    private readonly ILogger<PersonUniquenessGate> _logger;
    private readonly TimeProvider _clock;

    public PersonUniquenessGate(
        IKycIdentityStore identities,
        IPersonOperationClaimStore claims,
        IKycPersonKeyDeriver deriver,
        ILogger<PersonUniquenessGate>? logger = null,
        TimeProvider? clock = null)
    {
        _identities = identities;
        _claims = claims;
        _deriver = deriver;
        _logger = logger ?? NullLogger<PersonUniquenessGate>.Instance;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AZOAResult<KycEligibilityDecision>> TryClaimAsync(
        PersonUniquenessRequirement requirement,
        Guid userId,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        var invalid = RejectProgrammingError(requirement, userId);
        if (invalid is not null)
            return invalid;

        var now = _clock.GetUtcNow();

        // D39/D40. Both checks are SUBJECT-INDEPENDENT — they are about the operation,
        // not about the human — so short-circuiting here reveals nothing and leaks no
        // timing. The window is enforced HERE, outside the caller, and again inside
        // the claim statement: a node handler that forgets its own window check still
        // cannot record a late decision.
        string? decision = null;
        if (requirement.Decision is not null)
        {
            decision = PersonUniquenessRequirement.CanonicalizeDecision(requirement.Decision);
            if (decision is null)
            {
                _logger.LogError(
                    "Refusing a uniqueness claim: the declared decision is unusable. cause={Cause}",
                    KycIneligibilityCause.UniquenessUnavailable);
                return Ineligible(KycIneligibilityCause.UniquenessUnavailable);
            }

            if (requirement.DecisionClosesAt is { } closesAt && now >= closesAt)
            {
                _logger.LogWarning(
                    "Refusing a uniqueness claim: the decision window closed at {ClosesAt}. cause={Cause}",
                    closesAt,
                    KycIneligibilityCause.UniquenessUnavailable);
                return Ineligible(KycIneligibilityCause.UniquenessUnavailable);
            }
        }

        var resolution = await ResolveAsync(requirement, userId, tenantId, ct);
        if (resolution.EarlyRefusal is not null)
            return Ineligible(resolution.EarlyRefusal.Value);

        var candidate = new PersonOperationClaim
        {
            Id = PersonOperationClaim.RecordIdFor(
                resolution.UniquenessScope,
                resolution.OperationKey,
                resolution.PersonKey),
            UniquenessScope = resolution.UniquenessScope,
            OperationKey = resolution.OperationKey,
            PersonKey = resolution.PersonKey,
            UserId = resolution.UserKey,
            TenantId = tenantId is null || tenantId == Guid.Empty ? null : tenantId.Value.ToString("N"),
            ClaimedAt = now,
            // D39: the decision travels WITH the claim, so one statement both takes
            // the claim and records the vote of record. DecidedAt is set by the store
            // from this same server clock; no client value reaches it.
            //
            // An UNRESOLVED subject carries NO decision. Its round trip still happens
            // — that is what keeps "no identity" and "already claimed" the same cost —
            // but it writes against a per-user sentinel person key, and a sentinel row
            // holding a decision is a live vote in the ledger cast by somebody with no
            // verified identity. The refusal below would be returned and the vote
            // would be counted anyway.
            DecisionOption = resolution.UnresolvedCause is null ? decision : null,
            DecisionClosesAt = resolution.UnresolvedCause is null && decision is not null
                ? requirement.DecisionClosesAt
                : null,
            CreatedDate = now,
            ModifiedDate = now,
        };

        // The claim round trip runs even when the person could not be resolved, so
        // "no identity" and "already claimed" cost the same two round trips. That
        // narrows the timing channel to sub-round-trip noise; it does not close it.
        var claimed = await _claims.TryClaimAsync(candidate, ct);
        if (claimed.IsError || claimed.Result is null)
        {
            LogOperatorCause(
                requirement,
                userId,
                resolution,
                KycIneligibilityCause.UniquenessUnavailable,
                "the claim ledger was unreachable");
            return Ineligible(KycIneligibilityCause.UniquenessUnavailable);
        }

        if (resolution.UnresolvedCause is { } unresolved)
        {
            LogOperatorCause(requirement, userId, resolution, unresolved, "the person could not be resolved");
            return Ineligible(unresolved);
        }

        // The refusal is decided by READING THE WRITE BACK. A preceding "is it free?"
        // read is passed by both concurrent claimants and would make this a tautology.
        if (string.Equals(claimed.Result.UserId, resolution.UserKey, StringComparison.Ordinal))
        {
            // Same discipline for the decision: the store applies it under its own
            // window guard, so the ONLY way to know it landed is to read it back.
            // Reporting Eligible on a decision the store refused is exactly the
            // "claimed in prose, not enforced in code" failure this track exists to end.
            if (decision is not null
                && !string.Equals(claimed.Result.DecisionOption, decision, StringComparison.Ordinal))
            {
                LogOperatorCause(
                    requirement,
                    userId,
                    resolution,
                    KycIneligibilityCause.UniquenessUnavailable,
                    "the decision was refused by the claim ledger (the window is closed or the claim is frozen)");
                return Ineligible(KycIneligibilityCause.UniquenessUnavailable);
            }

            return AZOAResult<KycEligibilityDecision>.Success(KycEligibilityDecision.Eligible());
        }

        LogOperatorCause(
            requirement,
            userId,
            resolution,
            KycIneligibilityCause.PersonAlreadyClaimedOperation,
            "this person already holds the claim through another user");
        return Ineligible(KycIneligibilityCause.PersonAlreadyClaimedOperation);
    }

    public async Task<AZOAResult<KycEligibilityDecision>> EvaluateAsync(
        PersonUniquenessRequirement requirement,
        Guid userId,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        var invalid = RejectProgrammingError(requirement, userId);
        if (invalid is not null)
            return invalid;

        var resolution = await ResolveAsync(requirement, userId, tenantId, ct);
        if (resolution.EarlyRefusal is not null)
            return Ineligible(resolution.EarlyRefusal.Value);

        var existing = await _claims.GetAsync(
            resolution.UniquenessScope,
            resolution.OperationKey,
            resolution.PersonKey,
            ct);
        if (existing.IsError)
        {
            LogOperatorCause(
                requirement,
                userId,
                resolution,
                KycIneligibilityCause.UniquenessUnavailable,
                "the claim ledger was unreachable");
            return Ineligible(KycIneligibilityCause.UniquenessUnavailable);
        }

        if (resolution.UnresolvedCause is { } unresolved)
        {
            LogOperatorCause(requirement, userId, resolution, unresolved, "the person could not be resolved");
            return Ineligible(unresolved);
        }

        var claim = existing.Result;
        var free = claim is null
            || claim.ReleasedAt is not null
            || string.IsNullOrWhiteSpace(claim.UserId);
        if (free || string.Equals(claim!.UserId, resolution.UserKey, StringComparison.Ordinal))
            return AZOAResult<KycEligibilityDecision>.Success(KycEligibilityDecision.Eligible());

        LogOperatorCause(
            requirement,
            userId,
            resolution,
            KycIneligibilityCause.PersonAlreadyClaimedOperation,
            "this person already holds the claim through another user");
        return Ineligible(KycIneligibilityCause.PersonAlreadyClaimedOperation);
    }

    public async Task<AZOAResult<bool>> ReleaseAsync(
        PersonUniquenessRequirement requirement,
        Guid userId,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (userId == Guid.Empty)
            return AZOAResult<bool>.Failure("A user is required to release a claim.");

        var resolution = await ResolveAsync(requirement, userId, tenantId, ct);
        if (resolution.EarlyRefusal is not null)
            return AZOAResult<bool>.Success(false, "Nothing to release.");

        // The release uses the SAME key resolution as the claim, including the
        // sentinel, so a compensation also clears the row an unresolved attempt left
        // behind. A store fault is surfaced as an error rather than swallowed: a
        // failed compensation locks the person out of the operation, and the caller
        // must be able to see and log that. The caller still returns its own generic
        // sentence to the consumer.
        return await _claims.ReleaseAsync(
            resolution.UniquenessScope,
            resolution.OperationKey,
            resolution.PersonKey,
            resolution.UserKey,
            _clock.GetUtcNow(),
            ct);
    }

    /// <summary>
    /// IsError is reserved for programming errors — conditions that cannot depend on
    /// whether the subject exists elsewhere. Everything subject-dependent is a
    /// two-valued refusal, because an error result travels a different controller
    /// path (4xx/5xx vs a 200 carrying IsEligible=false) and that status-code
    /// difference is a perfectly usable oracle.
    /// </summary>
    private static AZOAResult<KycEligibilityDecision>? RejectProgrammingError(
        PersonUniquenessRequirement requirement,
        Guid userId)
    {
        if (requirement is null)
            return AZOAResult<KycEligibilityDecision>.Failure("A uniqueness requirement is required.");
        if (userId == Guid.Empty)
            return AZOAResult<KycEligibilityDecision>.Failure("A user is required to evaluate uniqueness.");
        return null;
    }

    /// <summary>
    /// Steps (a)-(c) of the resolution path, always in the same order. Only
    /// SUBJECT-INDEPENDENT problems (an unusable requirement, an unconfigured node)
    /// short-circuit here, because their timing reveals nothing about the person;
    /// a missing or erased identity does NOT short-circuit and instead carries a
    /// sentinel key into the claim round trip.
    /// </summary>
    private async Task<Resolution> ResolveAsync(
        PersonUniquenessRequirement requirement,
        Guid userId,
        Guid? tenantId,
        CancellationToken ct)
    {
        var userKey = userId.ToString("N");
        var operationKey = PersonUniquenessRequirement.Canonicalize(requirement.OperationKey);
        if (operationKey is null)
        {
            _logger.LogError(
                "Refusing a uniqueness check: the declared operation key is unusable. cause={Cause}",
                KycIneligibilityCause.UniquenessUnavailable);
            return Resolution.Early(KycIneligibilityCause.UniquenessUnavailable);
        }

        if (!_deriver.IsConfigured)
        {
            _logger.LogError(
                "Refusing a uniqueness check: the person-key root secret is unconfigured. operation={OperationKey}; cause={Cause}",
                operationKey,
                KycIneligibilityCause.UniquenessUnavailable);
            return Resolution.Early(KycIneligibilityCause.UniquenessUnavailable);
        }

        // (a) Reuse the ONE canonical scope rule. A second private copy of it is how
        // two tables end up asking their question in different scopes.
        var tenantScope = KycPersonKeyDeriver.ScopeValue(tenantId);
        var global = requirement.Scope == PersonUniquenessScope.Global;
        var uniquenessScope = global ? _deriver.GlobalScope : tenantScope;
        if (global)
        {
            // Every cross-tenant question the node is ever asked is auditable.
            _logger.LogWarning(
                "Resolving a NODE-GLOBAL uniqueness claim. operation={OperationKey}; user={UserId}; tenant={TenantId}",
                operationKey,
                userId,
                tenantId);
        }

        // (b) The reverse lookup: acting user -> their identity row in the acting
        // tenant's scope. Without it nothing could resolve an acting user to a
        // person key at all.
        var identity = await _identities.GetByUserAsync(tenantScope, userKey, ct);
        if (identity.IsError)
        {
            _logger.LogError(
                "Uniqueness resolution could not read the identity binding. operation={OperationKey}; user={UserId}; cause={Cause}",
                operationKey,
                userId,
                KycIneligibilityCause.UniquenessUnavailable);
            return Resolution.Unresolved(
                uniquenessScope, operationKey, userKey, KycIneligibilityCause.UniquenessUnavailable);
        }

        if (identity.Result is null)
        {
            return Resolution.Unresolved(
                uniquenessScope, operationKey, userKey, KycIneligibilityCause.NoVerifiedIdentity);
        }

        if (identity.Result.ErasedAt is not null)
        {
            return Resolution.Unresolved(
                uniquenessScope, operationKey, userKey, KycIneligibilityCause.IdentityErased);
        }

        // (c) The scope picks the key. This is the first production wiring of
        // global_person_key: a node-global claim is the only thing that legitimately
        // links one human across tenants, and it happens only on an explicit opt-in.
        var personKey = global ? identity.Result.GlobalPersonKey : identity.Result.PersonKey;
        if (string.IsNullOrWhiteSpace(personKey))
        {
            _logger.LogError(
                "Uniqueness resolution found an identity with no usable {Which} person key. operation={OperationKey}; user={UserId}; cause={Cause}",
                global ? "global" : "tenant",
                operationKey,
                userId,
                KycIneligibilityCause.UniquenessUnavailable);
            return Resolution.Unresolved(
                uniquenessScope, operationKey, userKey, KycIneligibilityCause.UniquenessUnavailable);
        }

        return new Resolution(uniquenessScope, operationKey, personKey, userKey, null, null);
    }

    private void LogOperatorCause(
        PersonUniquenessRequirement requirement,
        Guid userId,
        Resolution resolution,
        KycIneligibilityCause cause,
        string reason)
    {
        // The person key is NEVER logged; the operation key, the scope, and the
        // acting user are enough for support and carry no cross-tenant fact.
        _logger.LogWarning(
            "Refusing a per-operation uniqueness claim because {Reason}. operation={OperationKey}; scope={Scope}; declaredScope={DeclaredScope}; user={UserId}; cause={Cause}",
            reason,
            resolution.OperationKey,
            resolution.UniquenessScope,
            requirement.Scope,
            userId,
            cause);
    }

    private static AZOAResult<KycEligibilityDecision> Ineligible(KycIneligibilityCause cause)
        => AZOAResult<KycEligibilityDecision>.Success(KycEligibilityDecision.Ineligible(cause));

    /// <summary>
    /// Stand-in person key for an unresolvable subject, derived from the acting user
    /// so the claim round trip is real and repeatable. It is per-user, so it never
    /// answers "eligible" for someone with no identity — the resolution's
    /// <see cref="Resolution.UnresolvedCause"/> is what decides, not the claim.
    /// </summary>
    private static string SentinelPersonKey(string userKey)
    {
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userKey));
        return SentinelPersonKeyPrefix + Convert.ToHexString(digest).ToLowerInvariant()[..32];
    }

    private readonly record struct Resolution(
        string UniquenessScope,
        string OperationKey,
        string PersonKey,
        string UserKey,
        KycIneligibilityCause? UnresolvedCause,
        KycIneligibilityCause? EarlyRefusal)
    {
        /// <summary>A subject-independent refusal; no round trip is owed.</summary>
        public static Resolution Early(KycIneligibilityCause cause)
            => new(string.Empty, string.Empty, string.Empty, string.Empty, null, cause);

        /// <summary>
        /// A subject-DEPENDENT refusal. It still carries a usable scope, operation
        /// key, and sentinel person key so the caller performs the identical claim
        /// round trip an eligible subject would.
        /// </summary>
        public static Resolution Unresolved(
            string uniquenessScope,
            string operationKey,
            string userKey,
            KycIneligibilityCause cause)
            => new(uniquenessScope, operationKey, SentinelPersonKey(userKey), userKey, cause, null);
    }
}
