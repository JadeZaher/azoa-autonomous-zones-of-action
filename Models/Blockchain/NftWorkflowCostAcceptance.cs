using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>Explicit cost acceptance for one exact frozen workflow execution version.</summary>
public sealed record NftWorkflowCostAcceptance(Guid RequestId, Guid ExecutionId, long ExpectedExecutionVersion,
    NftProtocolCostAuthorization Authorization);

/// <summary>Immutable audit binding both original workflow authority and the actual accepting participant.</summary>
public sealed record NftWorkflowCostAcceptanceReceipt(int Version, NftWorkflowCostAcceptance Request,
    ActionExternalOperationReference Reference, WorkflowSigningWitness OriginalAuthority,
    SigningConsentWitness AcceptingAuthority, NftProtocolCostQuote Quote, DateTimeOffset CreatedAt);
