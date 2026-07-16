namespace AZOA.WebAPI.Models;

public class AvatarLoginModel
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>One-time proof for the configured operator bootstrap only.</summary>
    public string? BootstrapSecret { get; set; }
}
