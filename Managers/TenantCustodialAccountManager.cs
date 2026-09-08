using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Kyc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace AZOA.WebAPI.Managers;

/// <summary>Composes existing tenant, wallet, and KYC aggregates into one safe onboarding resource.</summary>
public sealed class TenantCustodialAccountManager : ITenantCustodialAccountManager
{
    private const string EnsureOperation = "tenant_custodial_ensure";
    private const string KycSessionOperation = "tenant_custodial_kyc_session";
    private const int LiveClaimReadAttempts = 40;
    private static readonly TimeSpan LiveClaimReadInterval = TimeSpan.FromMilliseconds(25);
    private readonly ITenantManager _tenants;
    private readonly IWalletManager _wallets;
    private readonly IKycManager _kyc;
    private readonly IBlockchainProviderFactory _providers;
    private readonly IIdempotencyStore _idempotency;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly IKycScreeningStore? _screening;
    private readonly ILogger<TenantCustodialAccountManager> _logger;

    public TenantCustodialAccountManager(
        ITenantManager tenants,
        IWalletManager wallets,
        IKycManager kyc,
        IBlockchainProviderFactory providers,
        IIdempotencyStore idempotency,
        IConfiguration configuration,
        IHostEnvironment environment,
        IKycScreeningStore? screening = null,
        ILogger<TenantCustodialAccountManager>? logger = null)
    {
        _tenants = tenants;
        _wallets = wallets;
        _kyc = kyc;
        _providers = providers;
        _idempotency = idempotency;
        _configuration = configuration;
        _environment = environment;
        _screening = screening;
        _logger = logger ?? NullLogger<TenantCustodialAccountManager>.Instance;
    }

    /// <inheritdoc/>
    public TenantCustodialCapabilitiesResponse GetCapabilities()
        => BuildCapabilities(_kyc.GetCapabilities());

    public async Task<AZOAResult<TenantCustodialCapabilitiesResponse>> GetCapabilitiesAsync(
        Guid tenantId,
        CancellationToken ct = default)
    {
        var resolved = await _kyc.GetCapabilitiesAsync(tenantId, ct);
        var kyc = resolved.IsError || resolved.Result is null
            ? new KycProviderCapabilitiesModel
            {
                Available = false,
                UnavailableReason = resolved.Message,
            }
            : resolved.Result;
        return AZOAResult<TenantCustodialCapabilitiesResponse>.Success(BuildCapabilities(kyc));
    }

    private TenantCustodialCapabilitiesResponse BuildCapabilities(
        KycProviderCapabilitiesModel kyc)
    {
        var enabled = _configuration.GetValue("CustodialAccounts:Enabled", true);
        var chain = WalletBootstrapIdentity.CanonicalChain(
            _configuration["CustodialAccounts:WalletChain"]
            ?? _configuration["Blockchain:DefaultChain"]);
        // One custody answer for the whole node (W1.8). Re-deriving the predicate
        // here is how "walletReady is false by construction outside Development"
        // survived a production custody mode being configured.
        var custody = CustodyModeResolver.Resolve(_configuration, _environment);
        var blockchainProviderAvailable = chain is not null && ResolveProviderAvailable(chain);
        var walletReason = FirstReason(
            (!enabled, "Tenant custodial accounts are disabled."),
            (chain is null, "The custodial wallet chain is unsupported."),
            (!custody.Available, TenantSafeCustodyReason(custody)),
            (!blockchainProviderAvailable, "The configured blockchain provider is unavailable."));
        var identityReady = enabled;
        var kycReady = enabled && kyc.Available;
        var walletProvisioningReady = walletReason is null;
        var reason = FirstReason(
            (!identityReady, "Tenant identity provisioning is disabled."),
            (!kyc.Available, kyc.UnavailableReason ?? "The KYC provider is unavailable."));
        reason ??= walletReason;

        return new TenantCustodialCapabilitiesResponse
        {
            Enabled = enabled,
            WalletChain = chain ?? string.Empty,
            CustodyMode = custody.ConfiguredMode,
            CustodyAvailable = custody.Available,
            BlockchainProviderAvailable = blockchainProviderAvailable,
            KycProvider = kyc.ProviderKey,
            KycAvailable = kyc.Available,
            HostedVerification = kyc.HostedVerification,
            AcceptsDocumentReferences = kyc.AcceptsDocumentReferences,
            DevelopmentSimulation = kyc.DevelopmentSimulation,
            IdentityReady = identityReady,
            KycReady = kycReady,
            WalletProvisioningReady = walletProvisioningReady,
            Ready = reason is null,
            UnavailableReason = reason
        };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<TenantCustodialAccountStatusResponse>> EnsureAsync(
        Guid tenantId,
        string externalSubject,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var validation = ValidateResourceIdentity(tenantId, externalSubject, idempotencyKey);
        if (validation is not null)
            return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(validation);

        externalSubject = externalSubject.Trim();
        var capabilities = (await GetCapabilitiesAsync(tenantId, ct)).Result!;
        if (!capabilities.IdentityReady)
            return AZOAResult<TenantCustodialAccountStatusResponse>.Success(
                UnavailableStatus(tenantId, externalSubject, capabilities.UnavailableReason));

        var operationKey = BuildOperationKey(tenantId, idempotencyKey);
        var operationType = BuildEnsureOperationType(externalSubject);
        var ownsUnsettledClaim = false;
        try
        {
            var claim = await _idempotency.TryClaimAsync(
                operationKey,
                operationType,
                CancellationToken.None);
            if (!claim.Won)
            {
                return await ReplayEnsureAsync(
                    claim.Record,
                    operationKey,
                    operationType,
                    tenantId,
                    externalSubject,
                    capabilities,
                    ct);
            }

            ownsUnsettledClaim = true;
            var result = await ConvergeAndSettleEnsureAsync(
                operationKey,
                tenantId,
                externalSubject,
                capabilities,
                CancellationToken.None);
            ownsUnsettledClaim = false;
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException)
        {
            await TryFailOwnedClaimAsync(
                ownsUnsettledClaim,
                operationKey,
                "Tenant custodial account provisioning was interrupted unexpectedly.",
                "EnsureIdempotencyCancellationRecovery",
                tenantId);

            throw;
        }
        catch (Exception ex)
        {
            LogUnexpected(ex, nameof(EnsureAsync), tenantId);
            await TryFailOwnedClaimAsync(
                ownsUnsettledClaim,
                operationKey,
                "Tenant custodial account provisioning failed unexpectedly.",
                "EnsureIdempotencyRecovery",
                tenantId);

            return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(
                "TENANT_CUSTODY_UNAVAILABLE: Custodial onboarding is temporarily unavailable.");
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<TenantCustodialAccountStatusResponse>> GetStatusAsync(
        Guid tenantId,
        string externalSubject,
        CancellationToken ct = default)
    {
        var validation = ValidateResourceIdentity(tenantId, externalSubject, idempotencyKey: null);
        if (validation is not null)
            return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(validation);

        externalSubject = externalSubject.Trim();
        var child = await _tenants.ResolveChildAsync(tenantId, externalSubject, ct);
        if (child.IsError || child.Result is null)
            return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(
                SafeIdentityMessage(child.Message));

        var capabilities = (await GetCapabilitiesAsync(tenantId, ct)).Result!;
        var wallet = await FindBootstrapWalletAsync(child.Result.UserId, capabilities.WalletChain);
        if (wallet.IsError)
            return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(
                "TENANT_WALLET_UNAVAILABLE: Custodial wallet status is temporarily unavailable.");

        return await BuildStatusAsync(
            tenantId,
            externalSubject,
            child.Result.UserId,
            wallet.Result,
            capabilities,
            ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<TenantKycSessionResponse>> BeginKycAsync(
        Guid tenantId,
        string externalSubject,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var validation = ValidateResourceIdentity(tenantId, externalSubject, idempotencyKey);
        if (validation is not null)
            return AZOAResult<TenantKycSessionResponse>.Failure(validation);

        externalSubject = externalSubject.Trim();
        var child = await ResolveOwnedChildAsync(tenantId, externalSubject, ct);
        if (child.IsError || child.Result is null)
            return AZOAResult<TenantKycSessionResponse>.Failure(SafeIdentityMessage(child.Message));

        var operationKey = BuildKycSessionOperationKey(tenantId, idempotencyKey);
        var operationType = BuildKycSessionOperationType(externalSubject);
        var ownsUnsettledClaim = false;
        try
        {
            var claim = await _idempotency.TryClaimAsync(operationKey, operationType, CancellationToken.None);
            if (!claim.Won
                && !string.Equals(claim.Record.OperationType, operationType, StringComparison.Ordinal))
            {
                return AZOAResult<TenantKycSessionResponse>.Failure(
                    "Idempotency-Key is already bound to a different KYC session request.");
            }

            if (!claim.Won && claim.Record.State == IdempotencyState.InProgress)
            {
                return await ReplayLiveKycClaimAsync(
                    claim.Record,
                    operationKey,
                    operationType,
                    ct);
            }

            if (!claim.Won && claim.Record.State == IdempotencyState.Failed)
            {
                return AZOAResult<TenantKycSessionResponse>.Failure(
                    "KYC_SESSION_UNAVAILABLE: The original KYC session request failed.");
            }

            ownsUnsettledClaim = claim.Won;

            // The stable key means "ensure an active session", not "replay one
            // immutable HTTP response". IKycManager owns durable attempt reuse
            // and server-side rollover after rejection/expiry.
            var started = await _kyc.BeginAsync(
                child.Result.UserId,
                tenantId,
                claim.Won ? CancellationToken.None : ct);
            if (started.IsError || started.Result is null)
            {
                const string unavailable = "KYC_SESSION_UNAVAILABLE: A KYC verification session could not be started.";
                if (ownsUnsettledClaim)
                {
                    await _idempotency.FailAsync(operationKey, unavailable, CancellationToken.None);
                    ownsUnsettledClaim = false;
                    ct.ThrowIfCancellationRequested();
                }
                return AZOAResult<TenantKycSessionResponse>.Failure(unavailable);
            }

            var response = new TenantKycSessionResponse
            {
                Provider = started.Result.ProviderKey,
                HostedVerification = started.Result.HostedVerification,
                AcceptsDocumentReferences = started.Result.AcceptsDocumentReferences,
                DevelopmentSimulation = started.Result.DevelopmentSimulation,
                VerificationUrl = started.Result.VerificationUrl,
                ExpiresAt = started.Result.ExpiresAt,
                Instructions = started.Result.Instructions
            };
            if (ownsUnsettledClaim)
            {
                await _idempotency.CompleteAsync(
                    operationKey,
                    IdempotencyReplay.SerializeForReplay(response),
                    CancellationToken.None);
                ownsUnsettledClaim = false;
                ct.ThrowIfCancellationRequested();
            }
            return AZOAResult<TenantKycSessionResponse>.Success(response);
        }
        catch (OperationCanceledException)
        {
            await TryFailOwnedClaimAsync(
                ownsUnsettledClaim,
                operationKey,
                "KYC_SESSION_UNAVAILABLE: KYC session provisioning was interrupted unexpectedly.",
                "KycSessionIdempotencyCancellationRecovery",
                tenantId);

            throw;
        }
        catch (Exception ex)
        {
            LogUnexpected(ex, nameof(BeginKycAsync), tenantId);
            await TryFailOwnedClaimAsync(
                ownsUnsettledClaim,
                operationKey,
                "KYC_SESSION_UNAVAILABLE: A KYC verification session could not be started.",
                "KycSessionIdempotencyRecovery",
                tenantId);

            return AZOAResult<TenantKycSessionResponse>.Failure(
                "KYC_SESSION_UNAVAILABLE: A KYC verification session could not be started.");
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<TenantKycSubmissionResponse>> SubmitKycAsync(
        Guid tenantId,
        string externalSubject,
        TenantKycSubmissionRequest request,
        CancellationToken ct = default)
    {
        var child = await ResolveOwnedChildAsync(tenantId, externalSubject, ct);
        if (child.IsError || child.Result is null)
            return AZOAResult<TenantKycSubmissionResponse>.Failure(SafeIdentityMessage(child.Message));

        if (request?.Documents is null)
            return AZOAResult<TenantKycSubmissionResponse>.Failure("A document reference list is required.");
        if (request.Documents.Any(document => document is null))
        {
            return AZOAResult<TenantKycSubmissionResponse>.Failure(
                "KYC document references must not contain null entries.");
        }

        var current = await _kyc.GetStatusAsync(child.Result.UserId, tenantId, ct);
        if (!current.IsError && current.Result is not null
            && current.Result.Status is KycStatus.PENDING or KycStatus.IN_REVIEW or KycStatus.APPROVED)
        {
            if (current.Result.Status == KycStatus.APPROVED
                || current.Result.Documents.Count > 0)
            {
                return AZOAResult<TenantKycSubmissionResponse>.Success(ToKycSubmission(current.Result));
            }
        }
        if (current.IsError
            && !current.Message.StartsWith(KycAuthorizationError.NotFound, StringComparison.Ordinal))
        {
            return AZOAResult<TenantKycSubmissionResponse>.Failure(
                SafeKycMessage(current.Message, current.Exception));
        }

        var model = new SubmitKycModel
        {
            Documents = request.Documents.Select(document => new SubmitKycDocumentModel
            {
                Type = document.Type,
                FileUrl = document.ReferenceUrl,
                FileName = document.FileName,
                MimeType = document.MimeType,
                FileSizeBytes = document.FileSizeBytes
            }).ToList()
        };
        var submitted = await _kyc.SubmitAsync(model, child.Result.UserId, tenantId, ct);
        if (submitted.IsError || submitted.Result is null)
            return AZOAResult<TenantKycSubmissionResponse>.Failure(
                SafeKycMessage(submitted.Message, submitted.Exception));

        return AZOAResult<TenantKycSubmissionResponse>.Success(ToKycSubmission(submitted.Result));
    }

    private async Task<AZOAResult<TenantCustodialAccountStatusResponse>> BuildStatusAsync(
        Guid tenantId,
        string externalSubject,
        Guid userId,
        IWallet? wallet,
        TenantCustodialCapabilitiesResponse capabilities,
        CancellationToken ct)
    {
        var kyc = await _kyc.GetStatusAsync(userId, tenantId, ct);
        var kycStatus = TenantKycStatus.Unknown;
        if (!kyc.IsError && kyc.Result is not null)
        {
            kycStatus = ToTenantStatus(kyc.Result.Status);
        }
        else if (!kyc.Message.StartsWith(KycAuthorizationError.NotFound, StringComparison.Ordinal))
        {
            return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(
                "TENANT_KYC_UNAVAILABLE: KYC status is temporarily unavailable.");
        }

        var review = await ResolveAmlReviewAsync(userId, tenantId, kycStatus, ct);
        if (review.IsError)
            return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(review.Message);
        if (review.Result!.UnderReview)
            kycStatus = TenantKycStatus.UnderReview;

        var walletReady = capabilities.WalletProvisioningReady
            && wallet is not null
            && wallet.Id != Guid.Empty
            && wallet.UserId == userId
            && wallet.WalletType == WalletType.Platform
            && !string.IsNullOrWhiteSpace(wallet.Address)
            && !string.IsNullOrWhiteSpace(wallet.EncryptedPrivateKey);
        // Under review comes first: it is the one state the subject cannot resolve
        // themselves, so it is the one they must be told about by name.
        var unavailableReason = capabilities.UnavailableReason
            ?? (kycStatus == TenantKycStatus.UnderReview ? KycAmlClearance.UnderReviewReason
                : !walletReady ? "The custodial wallet is not ready."
                : kycStatus != TenantKycStatus.Approved ? "KYC verification is not approved."
                : null);

        return AZOAResult<TenantCustodialAccountStatusResponse>.Success(new TenantCustodialAccountStatusResponse
        {
            TenantId = tenantId.ToString("D"),
            ExternalSubject = externalSubject,
            UserId = userId.ToString("D"),
            WalletId = wallet?.Id.ToString("D"),
            WalletAddress = wallet?.Address,
            KycStatus = kycStatus,
            IdentityReady = true,
            KycReady = capabilities.KycReady,
            WalletReady = walletReady,
            Ready = capabilities.IdentityReady
                && capabilities.KycReady
                && walletReady
                && kycStatus == TenantKycStatus.Approved,
            UnavailableReason = unavailableReason,
            ReviewDueAt = review.Result.ReviewDueAt
        });
    }

    /// <summary>Whether an approved identity still owes a sanctions answer, and by when.</summary>
    private sealed record AmlReviewProjection(bool UnderReview, DateTimeOffset? ReviewDueAt);

    /// <summary>
    /// Identity and sanctions are two questions. An approved identity whose AML
    /// verdict is not clear must project UnderReview, or this response tells a sponsor
    /// they are ready while <see cref="KycGateService"/> denies every value
    /// action they attempt, with no visible reason and no route to a human. The AML
    /// finding itself is never projected - only that a review is open, and its SLA.
    /// </summary>
    private async Task<AZOAResult<AmlReviewProjection>> ResolveAmlReviewAsync(
        Guid userId,
        Guid? tenantId,
        TenantKycStatus kycStatus,
        CancellationToken ct)
    {
        if (kycStatus != TenantKycStatus.Approved)
            return AZOAResult<AmlReviewProjection>.Success(new AmlReviewProjection(false, null));

        if (_screening is null)
        {
            // The screening store is the only source of the AML answer. Without it
            // this projection cannot be computed, and guessing "clear" is the one
            // answer that would let a sanctioned subject read as ready.
            _logger.LogError(
                "No KYC screening store is registered; the tenant custodial projection cannot report AML clearance.");
            return AZOAResult<AmlReviewProjection>.Failure(
                "TENANT_KYC_UNAVAILABLE: KYC status is temporarily unavailable.");
        }

        var screening = await _screening.GetLatestResultAsync(userId, tenantId, ct);
        if (screening.IsError)
        {
            return AZOAResult<AmlReviewProjection>.Failure(
                "TENANT_KYC_UNAVAILABLE: KYC status is temporarily unavailable.");
        }

        if (KycAmlClearance.IsClear(screening.Result))
            return AZOAResult<AmlReviewProjection>.Success(new AmlReviewProjection(false, null));

        var sla = KycAmlClearance.ResolveManualReviewSla(_configuration);
        return AZOAResult<AmlReviewProjection>.Success(new AmlReviewProjection(
            true,
            KycAmlClearance.ReviewDueAt(screening.Result, sla)));
    }

    private async Task<AZOAResult<IWallet>> FindBootstrapWalletAsync(Guid userId, string chain)
    {
        if (string.IsNullOrWhiteSpace(chain))
            return AZOAResult<IWallet>.Success(null!, "Custodial wallet chain is unavailable.");

        var wallets = await _wallets.QueryAsync(new WalletQueryRequest { ChainType = chain }, userId);
        if (wallets.IsError)
            return AZOAResult<IWallet>.Failure(
                "TENANT_WALLET_UNAVAILABLE: Custodial wallet status is temporarily unavailable.");

        var expectedId = WalletBootstrapIdentity.For(userId, chain);
        return AZOAResult<IWallet>.Success(
            wallets.Result?.SingleOrDefault(wallet => wallet.Id == expectedId)!,
            "Success");
    }

    private async Task<AZOAResult<ChildUserResponse>> ResolveOwnedChildAsync(
        Guid tenantId,
        string externalSubject,
        CancellationToken ct)
    {
        var validation = ValidateResourceIdentity(tenantId, externalSubject, idempotencyKey: null);
        if (validation is not null)
            return AZOAResult<ChildUserResponse>.Failure(validation);

        return await _tenants.ResolveChildAsync(tenantId, externalSubject.Trim(), ct);
    }

    private bool ResolveProviderAvailable(string chain)
    {
        var networkName = _configuration["Blockchain:DefaultNetwork"];
        if (!Enum.TryParse<ChainNetwork>(networkName, ignoreCase: true, out var network))
            return false;

        try
        {
            return _providers.GetProvider(chain, network) is not null;
        }
        catch (BlockchainProviderNotFoundException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The resolver owns the verdict; this owns the wording a TENANT may read. Only
    /// the key-material and unrecognised-mode reasons are rewritten: one names a node
    /// configuration path and the other echoes an operator-chosen string, and both are
    /// operator information rather than tenant information.
    /// </summary>
    private static string TenantSafeCustodyReason(CustodyModeDecision custody)
    {
        const string generic = "Custodial key management is disabled or unavailable.";
        if (custody.UnavailableReason is null || custody.Mode == RecognisedCustodyMode.Unrecognised)
            return generic;

        return string.Equals(
            custody.UnavailableReason,
            CustodyModeResolver.WalletEncryptionKeyUnusableReason,
            StringComparison.Ordinal)
                ? generic
                : custody.UnavailableReason;
    }

    private static string? ValidateResourceIdentity(Guid tenantId, string externalSubject, string? idempotencyKey)
    {
        if (tenantId == Guid.Empty)
            return "An authenticated tenant is required.";

        if (string.IsNullOrWhiteSpace(externalSubject)
            || externalSubject.Trim().Length > 128
            || externalSubject.Any(char.IsControl))
        {
            return "externalSubject must be a non-empty value of at most 128 characters.";
        }

        if (idempotencyKey is not null
            && (idempotencyKey.Length is < 16 or > 200 || idempotencyKey.Any(char.IsWhiteSpace)))
        {
            return "Idempotency-Key must be a stable non-whitespace value between 16 and 200 characters.";
        }

        return null;
    }

    private static TenantCustodialAccountStatusResponse UnavailableStatus(
        Guid tenantId,
        string externalSubject,
        string? reason) => new()
    {
        TenantId = tenantId.ToString("D"),
        ExternalSubject = externalSubject,
        KycStatus = TenantKycStatus.Unknown,
        IdentityReady = false,
        KycReady = false,
        Ready = false,
        WalletReady = false,
        UnavailableReason = reason ?? "Tenant custodial onboarding is unavailable."
    };

    private static TenantKycSubmissionResponse ToKycSubmission(KycSubmissionModel submission) => new()
    {
        SubmissionId = submission.Id.ToString("D"),
        Status = ToTenantStatus(submission.Status),
        SubmittedAt = submission.SubmittedAt,
        ExpiresAt = submission.ExpiresAt
    };

    private static TenantKycStatus ToTenantStatus(KycStatus status) => status switch
    {
        KycStatus.PENDING or KycStatus.IN_REVIEW => TenantKycStatus.Pending,
        KycStatus.APPROVED => TenantKycStatus.Approved,
        KycStatus.REJECTED or KycStatus.EXPIRED => TenantKycStatus.Rejected,
        _ => TenantKycStatus.Unknown
    };

    private static string? FirstReason(params (bool Applies, string Reason)[] candidates)
        => candidates.FirstOrDefault(candidate => candidate.Applies).Reason;

    private static string BuildOperationKey(Guid tenantId, string idempotencyKey)
        => $"tenant-custody:{tenantId:N}:{IdempotencyReplay.ContentHash(idempotencyKey.Trim())}";

    private static string BuildEnsureOperationType(string externalSubject)
        => $"{EnsureOperation}_{IdempotencyReplay.ContentHash(externalSubject)}";

    private static string BuildKycSessionOperationKey(Guid tenantId, string idempotencyKey)
        => $"tenant-kyc-session:{tenantId:N}:{IdempotencyReplay.ContentHash(idempotencyKey.Trim())}";

    private static string BuildKycSessionOperationType(string externalSubject)
        => $"{KycSessionOperation}_{IdempotencyReplay.ContentHash(externalSubject)}";

    private async Task<AZOAResult<TenantCustodialAccountStatusResponse>> ReplayEnsureAsync(
        IdempotencyRecord record,
        string operationKey,
        string operationType,
        Guid tenantId,
        string externalSubject,
        TenantCustodialCapabilitiesResponse capabilities,
        CancellationToken ct)
    {
        var observedLiveClaim = record.State == IdempotencyState.InProgress;
        if (record.State == IdempotencyState.InProgress)
        {
            if (!string.Equals(record.OperationType, operationType, StringComparison.Ordinal))
            {
                return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(
                    "Idempotency-Key is already bound to a different custodial account request.");
            }

            record = await WaitForTerminalRecordAsync(operationKey, record, ct);
            if (record.State == IdempotencyState.InProgress)
            {
                return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(
                    TenantCustodialOperationError.CustodyInProgress
                    + "The original custodial account request is still running.");
            }

            if (!string.Equals(record.OperationType, operationType, StringComparison.Ordinal))
            {
                return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(
                    "Idempotency-Key is already bound to a different custodial account request.");
            }
        }

        if (record.State == IdempotencyState.Failed)
            return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(
                "TENANT_CUSTODY_UNAVAILABLE: The original custodial account request failed.");

        var original = string.IsNullOrWhiteSpace(record.ResultPayload)
            ? null
            : IdempotencyReplay.DeserializeForReplay<TenantCustodialAccountStatusResponse>(record.ResultPayload);
        if (original is null)
            return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(
                "The original custodial account response could not be replayed.");

        if (!string.Equals(original.TenantId, tenantId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(original.ExternalSubject, externalSubject, StringComparison.Ordinal))
        {
            return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(
                "Idempotency-Key is already bound to a different custodial account request.");
        }

        if (observedLiveClaim)
            return AZOAResult<TenantCustodialAccountStatusResponse>.Success(original);

        // Re-run create-only/idempotent stages so a previously identity-only
        // completion converges once custody becomes available. Cached data binds
        // the key to the resource; it never freezes readiness or skips a stage.
        if (!capabilities.IdentityReady)
        {
            return AZOAResult<TenantCustodialAccountStatusResponse>.Success(
                UnavailableStatus(tenantId, externalSubject, capabilities.UnavailableReason));
        }

        return await ConvergeAccountAsync(tenantId, externalSubject, capabilities, ct);
    }

    private async Task<AZOAResult<TenantKycSessionResponse>> ReplayLiveKycClaimAsync(
        IdempotencyRecord record,
        string operationKey,
        string operationType,
        CancellationToken ct)
    {
        record = await WaitForTerminalRecordAsync(operationKey, record, ct);
        if (record.State == IdempotencyState.InProgress)
        {
            return AZOAResult<TenantKycSessionResponse>.Failure(
                TenantCustodialOperationError.KycSessionInProgress
                + "The original KYC session request is still running.");
        }

        if (!string.Equals(record.OperationType, operationType, StringComparison.Ordinal))
        {
            return AZOAResult<TenantKycSessionResponse>.Failure(
                "Idempotency-Key is already bound to a different KYC session request.");
        }

        if (record.State == IdempotencyState.Failed)
        {
            return AZOAResult<TenantKycSessionResponse>.Failure(
                "KYC_SESSION_UNAVAILABLE: The original KYC session request failed.");
        }

        var original = string.IsNullOrWhiteSpace(record.ResultPayload)
            ? null
            : IdempotencyReplay.DeserializeForReplay<TenantKycSessionResponse>(record.ResultPayload);
        return original is null
            ? AZOAResult<TenantKycSessionResponse>.Failure(
                "The original KYC session response could not be replayed.")
            : AZOAResult<TenantKycSessionResponse>.Success(original);
    }

    private async Task<IdempotencyRecord> WaitForTerminalRecordAsync(
        string operationKey,
        IdempotencyRecord initial,
        CancellationToken ct)
    {
        var current = initial;
        for (var attempt = 0;
             attempt < LiveClaimReadAttempts && current.State == IdempotencyState.InProgress;
             attempt++)
        {
            await Task.Delay(LiveClaimReadInterval, ct);
            current = await _idempotency.GetAsync(operationKey, ct) ?? current;
        }

        return current;
    }

    private async Task TryFailOwnedClaimAsync(
        bool ownsClaim,
        string operationKey,
        string message,
        string operation,
        Guid tenantId)
    {
        if (!ownsClaim)
            return;

        try
        {
            await _idempotency.FailAsync(operationKey, message, CancellationToken.None);
        }
        catch (Exception recoveryException)
        {
            LogUnexpected(recoveryException, operation, tenantId);
        }
    }

    private async Task<AZOAResult<TenantCustodialAccountStatusResponse>> ConvergeAndSettleEnsureAsync(
        string operationKey,
        Guid tenantId,
        string externalSubject,
        TenantCustodialCapabilitiesResponse capabilities,
        CancellationToken ct)
    {
        var status = await ConvergeAccountAsync(tenantId, externalSubject, capabilities, ct);
        if (status.IsError || status.Result is null)
            return await FailEnsureAsync(operationKey, status.Message);

        await _idempotency.CompleteAsync(
            operationKey,
            IdempotencyReplay.SerializeForReplay(status.Result),
            CancellationToken.None);
        return status;
    }

    private async Task<AZOAResult<TenantCustodialAccountStatusResponse>> ConvergeAccountAsync(
        Guid tenantId,
        string externalSubject,
        TenantCustodialCapabilitiesResponse capabilities,
        CancellationToken ct)
    {
        var provisioned = await _tenants.ProvisionChildAsync(
            tenantId,
            new ProvisionChildModel { ExternalUserId = externalSubject },
            ct);
        if (provisioned.IsError || provisioned.Result is null)
        {
            return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(
                SafeIdentityMessage(provisioned.Message));
        }

        IWallet? wallet = null;
        if (capabilities.WalletProvisioningReady)
        {
            var bootstrapped = await _wallets.BootstrapWalletAsync(
                new WalletGenerateRequest
                {
                    ChainType = capabilities.WalletChain,
                    Label = $"{capabilities.WalletChain} custodial wallet",
                    IsDefault = true
                },
                provisioned.Result.UserId);
            if (bootstrapped.IsError || bootstrapped.Result is null)
            {
                return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(
                    "TENANT_WALLET_UNAVAILABLE: Custodial wallet provisioning is temporarily unavailable.");
            }
            wallet = bootstrapped.Result;
        }

        return await BuildStatusAsync(
            tenantId,
            externalSubject,
            provisioned.Result.UserId,
            wallet,
            capabilities,
            ct);
    }

    private async Task<AZOAResult<TenantCustodialAccountStatusResponse>> FailEnsureAsync(
        string operationKey,
        string? message)
    {
        var safeMessage = message ?? "Tenant custodial account operation failed.";
        await _idempotency.FailAsync(operationKey, safeMessage, CancellationToken.None);
        return AZOAResult<TenantCustodialAccountStatusResponse>.Failure(safeMessage);
    }

    private static string SafeIdentityMessage(string? message)
    {
        if (message?.StartsWith(TenantAuthorizationError.NotFound, StringComparison.Ordinal) == true
            || message?.StartsWith(TenantAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
        {
            return message!;
        }

        if (string.Equals(
                message,
                "The deterministic tenant identity is already bound to a different account.",
                StringComparison.Ordinal))
        {
            return message!;
        }

        return "TENANT_IDENTITY_UNAVAILABLE: Tenant identity persistence is temporarily unavailable.";
    }

    private static string SafeKycMessage(string? message, Exception? exception)
    {
        if (exception is not null
            || string.IsNullOrWhiteSpace(message)
            || message.StartsWith("KYC_STORE_UNAVAILABLE:", StringComparison.Ordinal))
        {
            return "TENANT_KYC_UNAVAILABLE: KYC processing is temporarily unavailable.";
        }

        return message!;
    }

    private void LogUnexpected(Exception exception, string operation, Guid tenantId)
    {
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        _logger.LogError(
            exception,
            "Tenant custodial operation {Operation} failed; correlation={CorrelationId}; tenant={TenantId}",
            operation,
            correlationId,
            tenantId);
    }
}
