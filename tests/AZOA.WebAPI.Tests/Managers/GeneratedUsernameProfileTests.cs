using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Validators;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// QA H4: a wallet-created user saves its profile while keeping its generated handle; the generated
/// namespaces stay closed to anyone choosing a NEW name, so a deterministic handle cannot be squatted.
/// </summary>
public sealed class GeneratedUsernameProfileTests
{
    private const string WalletHandle = "wallet-solana-7xkxtg2cw87d-1a2b3c4d";

    private readonly List<IUser> _users = [];
    private readonly UserManager _manager;

    public GeneratedUsernameProfileTests()
    {
        var store = new Mock<IUserStore>();
        store.Setup(item => item.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AZOAResult<IEnumerable<IUser>> { Result = _users.ToList() });
        store.Setup(item => item.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _users.FirstOrDefault(user => user.Id == id) is { } found
                ? AZOAResult<IUser>.Success(found) : AZOAResult<IUser>.Failure("User not found."));
        store.Setup(item => item.UpsertAsync(It.IsAny<IUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IUser user, CancellationToken _) =>
            {
                _users.RemoveAll(row => row.Id == user.Id);
                _users.Add(user);
                return AZOAResult<IUser>.Success(user);
            });
        _manager = new UserManager(store.Object, new ConfigurationBuilder().Build());
    }

    private User WalletUser()
    {
        var user = new User { Id = Guid.NewGuid(), Username = WalletHandle, Email = $"{WalletHandle}@wallet.azoa.local", IsActive = true };
        _users.Add(user);
        return user;
    }

    [Fact]
    public async Task A_wallet_user_saves_its_profile_resending_its_generated_handle()
    {
        var user = WalletUser();
        var update = new UserUpdateModel { Username = WalletHandle, Email = user.Email, Title = "Builder", FirstName = "Ada" };

        new UserUpdateValidator().Validate(update).IsValid.Should().BeTrue("the request the profile page sends must pass model validation");
        var saved = await _manager.UpdateAsync(user.Id, update, user.Id);

        saved.IsError.Should().BeFalse(saved.Message);
        _users.Single().Title.Should().Be("Builder");
        _users.Single().Username.Should().Be(WalletHandle);
    }

    [Fact]
    public async Task A_user_cannot_adopt_a_name_in_a_generated_namespace()
    {
        var user = WalletUser();

        var renamed = await _manager.UpdateAsync(user.Id, new UserUpdateModel { Username = "wallet-solana-someone-else-99" }, user.Id);

        renamed.IsError.Should().BeTrue();
        renamed.Message.Should().Be(UsernameRules.ReservedPrefixMessage);
        _users.Single().Username.Should().Be(WalletHandle);
    }

    public static TheoryData<string, bool, string?> RegistrationNames => new()
    {
        { "alice_smith", true, null },
        { "alice-smith", true, null },
        { "al", false, UsernameRules.PatternMessage },
        { "alice smith", false, UsernameRules.PatternMessage },
        { "wallet-solana-abc", false, UsernameRules.ReservedPrefixMessage },
        { "OAuth-anything", false, UsernameRules.ReservedPrefixMessage },
        { "onboard-1234", false, UsernameRules.ReservedPrefixMessage },
    };

    [Theory]
    [MemberData(nameof(RegistrationNames))]
    public void Registration_names_follow_one_alphabet_and_the_reserved_prefixes(string username, bool valid, string? message)
    {
        var result = new UserRegisterValidator().Validate(new UserRegisterModel
        {
            Username = username, Email = "a@example.com", Password = "Password1",
        });

        result.IsValid.Should().Be(valid);
        if (message is not null)
            result.Errors.Select(error => error.ErrorMessage).Should().Contain(message);
    }
}
