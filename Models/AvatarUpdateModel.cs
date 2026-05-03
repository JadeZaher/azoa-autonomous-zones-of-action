namespace OASIS.WebAPI.Models;

public class AvatarUpdateModel
{
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? Title { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public bool? IsActive { get; set; }
}
