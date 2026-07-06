namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Operator request to register (or re-register) a Holon AssetType in the opt-in
/// registry (final-hardening-cutover F5). <see cref="AssetType"/> is the natural
/// key. <see cref="RequiredMetadataFields"/> lists metadata keys a holon of this
/// type must carry (present + non-empty); empty ⇒ only the type name is validated.
/// </summary>
public class HolonTypeRegisterModel
{
    public string AssetType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> RequiredMetadataFields { get; set; } = new();
    public bool IsActive { get; set; } = true;
}
