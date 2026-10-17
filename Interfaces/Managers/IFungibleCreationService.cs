using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Admits explicitly authorized fungible creation and recovers its exact chain and local effects.</summary>
public interface IFungibleCreationService
{
    Task<AZOAResult<FungibleCreationQuote>> QuoteAsync(FungibleCreationIntent intent, CancellationToken ct = default);
    /// <summary>Accepts one immutable unexpired quote without signing or broadcasting.</summary>
    Task<AZOAResult<FungibleCreationView>> AdmitAsync(FungibleCreationCommand command, CancellationToken ct = default);
    /// <summary>Persists a current cost quote for the original participant without accepting it.</summary>
    Task<AZOAResult<FungibleCreationView>> OfferQuoteAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default);
    /// <summary>Accepts an exact displayed quote and version; signing remains a separate continuation.</summary>
    Task<AZOAResult<FungibleCreationView>> AcceptQuoteAsync(string id, Guid actor, Guid? tenant, long version, string quoteId, CancellationToken ct = default);
    /// <summary>Continues the participant's original operation with current readiness and signing authority.</summary>
    Task<AZOAResult<FungibleCreationView>> ContinueAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null, FungibleContinuationRequest? authorization = null);
    /// <summary>Observes, rebroadcasts authorized exact wire and projects confirmed evidence without fresh signatures.</summary>
    Task<AZOAResult<FungibleCreationView>> RefreshAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default);
    Task<AZOAResult<FungibleCreationView>> GetAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default);
    Task<IReadOnlyList<FungibleCreationView>> ListAsync(Guid actor, Guid? tenant, int offset, int limit, CancellationToken ct = default);
    Task<AZOAResult<FungibleCreationView>> CancelAsync(string id, FungibleContinuationRequest request,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness credential, CancellationToken ct = default);
    Task<AZOAResult<FungibleCreationView>> ReplaceAsync(string id, FungibleContinuationRequest request,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness credential, CancellationToken ct = default);
    Task<AZOAResult<FungibleAttachmentView>> GetAttachmentAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default);
    Task<AZOAResult<FungibleAttachmentView>> AttachAsync(string id, FungibleAttachmentRequest request,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness credential, CancellationToken ct = default);
}
