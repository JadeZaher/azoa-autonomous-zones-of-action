namespace AZOA.WebAPI.Models.Requests;

public class HolonInteractionRequest
{
    public List<Guid> AddPeerHolonIds { get; set; } = new();
    public List<Guid> RemovePeerHolonIds { get; set; } = new();
    public Guid? NewParentHolonId { get; set; }
    public Dictionary<string, string>? SetMetadata { get; set; }
    public List<string>? RemoveMetadataKeys { get; set; }
}
