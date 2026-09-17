using Moq;
using SAW.Application.Features.Authentication.Commands;
using SAW.Application.Features.Authentication.DTOs;
using SAW.Application.Repositories;
using SAW.Domain.Entities;

namespace SAW.Test.Application.Authentication;

public sealed class AuthCommandServiceTests
{
    private readonly Mock<IAuthRepository> _repository = new();
    private readonly Mock<IPasswordHasher> _passwordHasher = new();
    private readonly Mock<ITokenService> _tokenService = new();
    private readonly Mock<IEmailSender> _emailSender = new();
    private readonly Mock<IGoogleIdentityService> _googleIdentityService = new();

    [Fact]
    public async Task LoginAsync_WithActiveAccount_ReturnsSessionForAssignedRole()
    {
        var account = CreateAccount();
        _repository.Setup(x => x.GetAccountByIdentifierAsync("admin@saw.local", It.IsAny<CancellationToken>())).ReturnsAsync(account);
        _repository.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _passwordHasher.Setup(x => x.Verify("Admin@123", account.PasswordHash)).Returns(true);
        ConfigureTokenService();

        var result = await CreateService().LoginAsync(
            new LoginRequest("admin@saw.local", "Admin@123"), "127.0.0.1", CancellationToken.None);

        Assert.Equal("ADMINISTRATOR", result.User.Role);
        Assert.Equal("access-token", result.AccessToken);
        Assert.Equal("refresh-token", result.RefreshToken);
        _repository.Verify(x => x.AddRefreshToken(It.Is<RefreshToken>(token =>
            token.AccountId == account.AccountId && token.TokenHash == "hashed-refresh-token")), Times.Once);
    }

    [Fact]
    public async Task GoogleLoginAsync_WithUnknownAccountAndNoRole_RequestsRoleSelection()
    {
        _googleIdentityService.Setup(x => x.VerifyAsync("google-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleIdentity("google-subject", "new@saw.local", "New User"));
        _repository.Setup(x => x.GetAccountByEmailAsync("new@saw.local", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account?)null);

        var result = await CreateService().GoogleLoginAsync(
            new GoogleLoginRequest("google-token", null), "127.0.0.1", CancellationToken.None);

        Assert.True(result.RequiresRole);
        Assert.Null(result.Session);
        _repository.Verify(x => x.CreateRegistrationAsync(
            It.IsAny<Account>(), It.IsAny<Supplier?>(), It.IsAny<Distributor?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GoogleLoginAsync_WithNewSupplier_CreatesAccountAndSession()
    {
        var role = new Role { RoleId = 5, RoleCode = "SUPPLIER", RoleName = "Supplier", IsActive = true };
        _googleIdentityService.Setup(x => x.VerifyAsync("google-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleIdentity("google-subject", "supplier@saw.local", "Google Supplier"));
        _repository.Setup(x => x.GetAccountByEmailAsync("supplier@saw.local", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account?)null);
        _repository.Setup(x => x.GetActiveRoleAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(role);
        _repository.Setup(x => x.CreateRegistrationAsync(
                It.IsAny<Account>(), It.IsAny<Supplier?>(), It.IsAny<Distributor?>(), It.IsAny<CancellationToken>()))
            .Callback<Account, Supplier?, Distributor?, CancellationToken>((account, _, _, _) => account.AccountId = 7)
            .Returns(Task.CompletedTask);
        _repository.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _passwordHasher.Setup(x => x.Hash(It.IsAny<string>())).Returns("password-hash");
        ConfigureTokenService();

        var result = await CreateService().GoogleLoginAsync(
            new GoogleLoginRequest("google-token", 5), "127.0.0.1", CancellationToken.None);

        Assert.False(result.RequiresRole);
        Assert.NotNull(result.Session);
        Assert.Equal("SUPPLIER", result.Session.User.Role);
        _repository.Verify(x => x.CreateRegistrationAsync(
            It.Is<Account>(account => account.AccountStatus == "ACTIVE" && account.Role == role),
            It.Is<Supplier>(supplier => supplier.ProfileStatus == "PENDING_REVIEW"),
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private AuthCommandService CreateService() => new(
        _repository.Object, _passwordHasher.Object, _tokenService.Object,
        _emailSender.Object, _googleIdentityService.Object);

    private void ConfigureTokenService()
    {
        _tokenService.SetupGet(x => x.RefreshTokenDays).Returns(7);
        _tokenService.Setup(x => x.CreateAccessToken(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns(new AccessTokenResult("access-token", "jwt-id", DateTime.UtcNow.AddMinutes(30)));
        _tokenService.Setup(x => x.CreateOpaqueToken()).Returns("refresh-token");
        _tokenService.Setup(x => x.HashToken("refresh-token")).Returns("hashed-refresh-token");
    }

    private static Account CreateAccount() => new()
    {
        AccountId = 1,
        RoleId = 1,
        Username = "admin",
        Email = "admin@saw.local",
        PasswordHash = "password-hash",
        FullName = "SAW Administrator",
        AccountStatus = "ACTIVE",
        Role = new Role { RoleId = 1, RoleCode = "ADMINISTRATOR", RoleName = "Administrator", IsActive = true }
    };
}
