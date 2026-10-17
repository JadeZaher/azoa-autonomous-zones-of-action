using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Generates immutable runnable artifacts and admits recoverable caller-authorized deployments.</summary>
public interface IDeploymentService
{
    Task<AZOAResult<DeploymentArtifactView>> GenerateAsync(Guid bundleId, Guid actor, Guid? tenant, DeploymentGenerateRequest request, CancellationToken ct = default);
    Task<AZOAResult<DeploymentArtifactView>> GetArtifactAsync(string artifactId, Guid actor, Guid? tenant, CancellationToken ct = default);
    Task<AZOAResult<object>> QueryContractAsync(ContractQueryRequest request, CancellationToken ct = default);
    Task<AZOAResult<NativeOperationIntent>> QuoteAsync(Guid actor, Guid? tenant, DeploymentQuoteRequest request, CancellationToken ct = default);
    /// <summary>Admits and advances a request; native signing fails closed without a captured credential (use StartWithCredentialAsync).</summary>
    Task<AZOAResult<DeploymentReceipt>> StartAsync(Guid actor, Guid? tenant, string requestKey, DeploymentStartRequest request, CancellationToken ct = default);
    /// <summary>Admits and advances a request, binding every native signer to the caller's captured credential and scope.</summary>
    Task<AZOAResult<DeploymentReceipt>> StartWithCredentialAsync(Guid actor, Guid? tenant, string requestKey, DeploymentStartRequest request,
        SigningCredentialWitness? credential, CancellationToken ct = default);
    /// <summary>Claims the exact authorized request after an action crash without signing or submitting a transaction.</summary>
    Task<AZOAResult<DeploymentReceipt>> AdmitAsync(Guid actor, Guid? tenant, string requestKey, DeploymentStartRequest request, CancellationToken ct = default);
    /// <summary>Admits a frozen action request, claiming one authoritative quote; recovery cannot sign.</summary>
    Task<AZOAResult<DeploymentReceipt>> StartActionAsync(ActionExternalOperationReference reference, string canonicalInput, bool authorize, CancellationToken ct = default);
    Task<AZOAResult<DeploymentReceipt>> GetAsync(string operationId, Guid actor, Guid? tenant, CancellationToken ct = default);
    Task<AZOAResult<DeploymentReceipt>> GetRequestAsync(string requestKey, Guid actor, Guid? tenant, CancellationToken ct = default);
    Task<AZOAResult<DeploymentReceipt>> CancelRequestAsync(DeploymentRequestCancellation request, Guid actor, Guid? tenant, CancellationToken ct = default);
    Task<AZOAResult<DeploymentPage>> ListAsync(Guid actor, Guid? tenant, string? cursor = null, int limit = 25, CancellationToken ct = default);
    Task<AZOAResult<DeploymentReceipt>> ContinueAsync(string operationId, Guid actor, Guid? tenant, CancellationToken ct = default,
        SigningCredentialWitness? credential = null);
    Task<AZOAResult<DeploymentReceipt>> CancelAsync(string operationId, Guid actor, Guid? tenant, CancellationToken ct = default);
    Task<AZOAResult<DeploymentRecoveryQuote>> QuoteRecoveryAsync(string operationId, Guid actor, Guid? tenant,
        DeploymentRecoveryQuoteRequest request, CancellationToken ct = default);
    Task<AZOAResult<DeploymentReceipt>> RecoverWithinBudgetAsync(string operationId, Guid actor, Guid? tenant,
        DeploymentOriginalBudgetRecoveryRequest request, CancellationToken ct = default, SigningCredentialWitness? credential = null);
    Task<AZOAResult<DeploymentReceipt>> StartRecoveryAsync(string operationId, Guid actor, Guid? tenant,
        DeploymentRecoveryStartRequest request, CancellationToken ct = default, SigningCredentialWitness? credential = null);
    /// <summary>Invalidates an unadmitted exact recovery quote by advancing its parent admission fence.</summary>
    Task<AZOAResult<DeploymentReceipt>> InvalidateRecoveryAsync(string operationId, Guid actor, Guid? tenant,
        DeploymentRecoveryStartRequest request, CancellationToken ct = default);
    /// <summary>Inspects at most one durable page of the owned current recovery ancestry without signing.</summary>
    Task<AZOAResult<DeploymentRecoveryAncestry>> InspectRecoveryAncestryAsync(string operationId, Guid actor, Guid? tenant,
        CancellationToken ct = default);
    Task ObserveAsync(string operationId, CancellationToken ct = default);
    /// <summary>Operator view of native operations parked in NeedsOperator, oldest first.</summary>
    Task<IReadOnlyList<DeploymentReceipt>> ListNeedsOperatorAsync(int limit, CancellationToken ct = default);
    /// <summary>Re-observes a parked operation; only an unresolvable observation may be closed as terminal Failed.</summary>
    Task<AZOAResult<DeploymentReceipt>> ResolveNeedsOperatorAsync(string operationId, Guid operatorUserId,
        DeploymentOperatorResolution request, CancellationToken ct = default);
    Task<AZOAResult<ActionRun>> InvokeAsync(string operationId, Guid actor, Guid? tenant, string invocationKey,
        DeploymentInvokeRequest request, CancellationToken ct = default,
        SigningCredentialWitness? authenticationWitness = null);
    Task<AZOAResult<string>> GetLoaderAsync(string operationId, Guid actor, Guid? tenant, CancellationToken ct = default);
}
