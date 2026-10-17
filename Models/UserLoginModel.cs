namespace AZOA.WebAPI.Models;

public class UserLoginModel
{
    /// <summary>Stable login identity, mutually exclusive with email.</summary>
    public Guid? UserId { get; set; }

    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>One-time proof for the configured operator bootstrap only.</summary>
    public string? BootstrapSecret { get; set; }
}
