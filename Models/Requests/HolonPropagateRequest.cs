namespace AZOA.WebAPI.Models.Requests;

public class HolonPropagateRequest
{
    /// <summary>
    /// Property name to propagate. Supported: "IsActive".
    /// </summary>
    public string Property { get; set; } = "IsActive";

    /// <summary>
    /// Value to set on the holon and all descendants.
    /// </summary>
    public bool Value { get; set; } = true;

    /// <summary>
    /// If true, also apply to the target holon itself. If false, only descendants.
    /// </summary>
    public bool IncludeSelf { get; set; } = true;
}
