namespace AZOA.WebAPI.Models;

public class UserUpdateModel
{
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? Title { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public bool? IsActive { get; set; }
    /// Opt-in: appears in another caller's search results only when true. Absent = unchanged.
    public bool? Discoverable { get; set; }
}
