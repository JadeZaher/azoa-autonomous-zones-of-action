using System.Text.Json;
using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Models;

public class Holon : IHolon, INft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid? ParentHolonId { get; set; }
    public Holon? ParentHolon { get; set; }
    public List<Holon> SubHolons { get; set; } = new();
    public Guid? AvatarId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string? ChainId { get; set; }
    public string? AssetType { get; set; }
    public string? TokenId { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public List<Guid> PeerHolonIds { get; set; } = new();
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedDate { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? SourceHolonId { get; set; }
    public Guid? OriginAvatarId { get; set; }
    public bool IsPublic { get; set; } = false;
}
