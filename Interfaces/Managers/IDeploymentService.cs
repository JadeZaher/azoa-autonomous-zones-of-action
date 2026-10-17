using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Generates immutable runnable artifacts and admits recoverable caller-authorized deployments.</summary>
public interface IDeploymentService
{
    Task<AZOAResult<DeploymentArtifactView>> GenerateAsync(Guid bundleId, Guid actor, Guid? tenant, DeploymentGenerateRequest request, CancellationToken ct = default);
    Task<AZOAResult<DeploymentArtifactView>> GetArtifactAsync(string artifactId, Guid actor, Guid? tenant, CancellationToken ct = default);
    Task<AZOAResult<object>> QueryContractAsync(ContractQueryRequest request, CancellationToken ct = default);
    Task<AZOAResult<NativeOperationIntent>> QuoteAsync(Guid actor, Guid? tenant, DeploymentQuoteRequest request, CancellationToken ct = default);
    Task<AZOAResult<DeploymentReceipt>> StartAsync(Guid actor, Guid? tenant, string requestKey, DeploymentStartRequest request, CancellationToken ct = default);
    /// <summary>Claims the exact authorized request after an action crash without signing or submitting a transaction.</summary>
    Task<AZOAResult<DeploymentReceipt>> AdmitAsync(Guid actor, Guid? tenant, string requestKey, DeploymentStartRequest request, CancellationToken ct = default);
    /// <summary>Admits a frozen action request, claiming one authoritative quote; recovery cannot sign.</summary>
    Task<AZOAResult<DeploymentReceipt>> StartActionAsync(ActionExternalOperationReference reference, string canonicalInput, bool authorize, CancellationToken ct = default);
    Task<AZOAResult<DeploymentReceipt>> GetAsync(string operationId, Guid actor, Guid? tenant, CancellationToken ct = default);
    Task<AZOAResult<DeploymentReceipt>> GetRequestAsync(string requestKey, Guid actor, Guid? tenant, CancellationToken ct = default);
    Task<AZOAResult<DeploymentReceipt>> CancelRequestAsync(DeploymentRequestCancellation request, Guid actor, Guid? tenant, CancellationToken ct = default);
    Task<AZOAResult<DeploymentPage>> ListAsync(Guid actor, Guid? tenant, string? cursor = null, int limit = 25, CancellationToken ct = default);
    Task<AZOAResult<DeploymentReceipt>> ContinueAsync(string operationId, Guid actor, Guid? tenant, CancellationToken ct = default);
    Task<AZOAResult<DeploymentReceipt>> CancelAsync(string operationId, Guid actor, Guid? tenant, CancellationToken ct = default);
    Task<AZOAResult<DeploymentRecoveryQuote>> QuoteRecoveryAsync(string operationId, Guid actor, Guid? tenant,
        DeploymentRecoveryQuoteRequest request, CancellationToken ct = default);
    Task<AZOAResult<DeploymentReceipt>> RecoverWithinBudgetAsync(string operationId, Guid actor, Guid? tenant,
        DeploymentOriginalBudgetRecoveryRequest request, CancellationToken ct = default);
    Task<AZOAResult<DeploymentReceipt>> StartRecoveryAsync(string operationId, Guid actor, Guid? tenant,
        DeploymentRecoveryStartRequest request, CancellationToken ct = default);
    /// <summary>Invalidates an unadmitted exact recovery quote by advancing its parent admission fence.</summary>
    Task<AZOAResult<DeploymentReceipt>> InvalidateRecoveryAsync(string operationId, Guid actor, Guid? tenant,
        DeploymentRecoveryStartRequest request, CancellationToken ct = default);
    /// <summary>Inspects at most one durable page of the owned current recovery ancestry without signing.</summary>
    Task<AZOAResult<DeploymentRecoveryAncestry>> InspectRecoveryAncestryAsync(string operationId, Guid actor, Guid? tenant,
        CancellationToken ct = default);
    Task ObserveAsync(string operationId, CancellationToken ct = default);
    Task<AZOAResult<ActionRun>> InvokeAsync(string operationId, Guid actor, Guid? tenant, string invocationKey,
        DeploymentInvokeRequest request, CancellationToken ct = default,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness? authenticationWitness = null);
    Task<AZOAResult<string>> GetLoaderAsync(string operationId, Guid actor, Guid? tenant, CancellationToken ct = default);
}
