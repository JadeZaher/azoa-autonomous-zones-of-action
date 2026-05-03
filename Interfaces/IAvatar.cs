namespace OASIS.WebAPI.Interfaces;

public interface IAvatar
{
    Guid Id { get; set; }
    string Username { get; set; }
    string Email { get; set; }
    string PasswordHash { get; set; }
    string? Title { get; set; }
    string? FirstName { get; set; }
    string? LastName { get; set; }
    DateTime CreatedDate { get; set; }
    DateTime? LastBeamedInDate { get; set; }
    bool IsActive { get; set; }
    bool IsVerified { get; set; }
    int Karma { get; set; }
    int Level { get; set; }
}
