using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Accepted two-leg chain evidence that may be durably recorded under a settlement lease.</summary>
public sealed record NodeFeeAcceptedAtomicGroup(
    AtomicTransferGroupRequest Request,
    AtomicTransferGroupSubmission Submission);
