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
    public async Task GoogleLoginAsync_WithNewSupplier_SendsVerificationWithoutSession()
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

        var error = await Assert.ThrowsAsync<SAW.Application.Exceptions.ForbiddenException>(() => CreateService().GoogleLoginAsync(
            new GoogleLoginRequest("google-token", 5), "127.0.0.1", CancellationToken.None));
        Assert.Contains("xác nhận email", error.Message);
        _repository.Verify(x => x.CreateRegistrationAsync(
            It.Is<Account>(account => account.AccountStatus == "PENDING" && account.Role == role),
            It.Is<Supplier>(supplier => supplier.ProfileStatus == "PENDING_REVIEW"),
            null,
            It.IsAny<CancellationToken>()), Times.Once);
        _emailSender.Verify(x => x.SendEmailVerificationAsync("supplier@saw.local", "refresh-token", It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.AddRefreshToken(It.IsAny<RefreshToken>()), Times.Never);
    }

    [Fact]
    public async Task GoogleLoginAsync_PendingAccount_DoesNotBypassEmailVerification()
    {
        var account = CreateAccount(); account.AccountStatus = "PENDING";
        _googleIdentityService.Setup(x => x.VerifyAsync("token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleIdentity("subject", account.Email, account.FullName));
        _repository.Setup(x => x.GetAccountByEmailAsync(account.Email, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        var error = await Assert.ThrowsAsync<SAW.Application.Exceptions.ForbiddenException>(() => CreateService().GoogleLoginAsync(
            new GoogleLoginRequest("token", null), null, default));
        Assert.Contains("xác nhận email", error.Message);
        Assert.Equal("PENDING", account.AccountStatus);
        _repository.Verify(x => x.AddRefreshToken(It.IsAny<RefreshToken>()), Times.Never);
    }

    private AuthCommandService CreateService() => new(
        _repository.Object, _passwordHasher.Object, _tokenService.Object,
        _emailSender.Object, _googleIdentityService.Object);

    [Fact]
    public async Task GoogleLoginAsync_VerifiedGeneratedDistributorProfile_DoesNotRequireAdminApproval()
    {
        var account = CreateAccount(); account.Username = "google_existing";
        account.Role = new Role { RoleId = 6, RoleCode = "DISTRIBUTOR", RoleName = "Distributor" }; account.RoleId = 6;
        account.Distributor = new Distributor { TaxCode = "GOOGLE-existing", ProfileStatus = "PENDING_REVIEW" };
        _googleIdentityService.Setup(x => x.VerifyAsync("token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleIdentity("existing", account.Email, account.FullName));
        _repository.Setup(x => x.GetAccountByEmailAsync(account.Email, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        ConfigureTokenService();
        var response = await CreateService().GoogleLoginAsync(new GoogleLoginRequest("token", null), null, default);
        Assert.NotNull(response.Session);
        Assert.Equal("ACTIVE", account.Distributor.ProfileStatus);
    }

    [Theory]
    [InlineData("INACTIVE")]
    [InlineData("PENDING")]
    [InlineData("LOCKED")]
    public async Task LoginAsync_BlockedAccount_DoesNotVerifyPasswordOrCreateSession(string status)
    {
        var account = CreateAccount(); account.AccountStatus = status;
        _repository.Setup(x => x.GetAccountByIdentifierAsync("admin", It.IsAny<CancellationToken>())).ReturnsAsync(account);
        await Assert.ThrowsAsync<SAW.Application.Exceptions.ForbiddenException>(() => CreateService().LoginAsync(new("admin", "Admin@123"), null, default));
        _passwordHasher.VerifyNoOtherCalls();
        _repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(x => x.AddRefreshToken(It.IsAny<RefreshToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(3, 4, false)]
    [InlineData(4, 0, true)]
    public async Task LoginAsync_WrongPassword_CountsFailuresAndLocksAtFifthAttempt(int previous, int expected, bool locked)
    {
        var account = CreateAccount(); account.FailedLoginAttempts = previous;
        _repository.Setup(x => x.GetAccountByIdentifierAsync("admin", It.IsAny<CancellationToken>())).ReturnsAsync(account);
        var before = DateTime.UtcNow;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateService().LoginAsync(new(" admin ", "wrong"), null, default));
        Assert.Equal(expected, account.FailedLoginAttempts);
        if (locked) Assert.InRange(account.LockoutEnd!.Value, before.AddMinutes(15), DateTime.UtcNow.AddMinutes(15));
        else Assert.Null(account.LockoutEnd);
        Assert.NotNull(account.UpdatedAt);
        _repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.AddRefreshToken(It.IsAny<RefreshToken>()), Times.Never);
    }

    [Fact]
    public async Task LoginAsync_ActiveTemporaryLock_DoesNotCheckPassword()
    {
        var account = CreateAccount(); account.LockoutEnd = DateTime.UtcNow.AddMinutes(15);
        _repository.Setup(x => x.GetAccountByIdentifierAsync("admin", It.IsAny<CancellationToken>())).ReturnsAsync(account);
        await Assert.ThrowsAsync<SAW.Application.Exceptions.ForbiddenException>(() => CreateService().LoginAsync(new("admin", "Admin@123"), null, default));
        _passwordHasher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LoginAsync_ExpiredTemporaryLock_SuccessClearsFailuresAndReturnsCurrentProfile()
    {
        var account = CreateAccount(); account.LockoutEnd = DateTime.UtcNow.AddMinutes(-1); account.FailedLoginAttempts = 3;
        account.FullName = "Updated profile name";
        _repository.Setup(x => x.GetAccountByIdentifierAsync("admin", It.IsAny<CancellationToken>())).ReturnsAsync(account);
        _passwordHasher.Setup(x => x.Verify("Admin@123", account.PasswordHash)).Returns(true);
        ConfigureTokenService();
        var result = await CreateService().LoginAsync(new("admin", "Admin@123"), "127.0.0.7", default);
        Assert.Equal(0, account.FailedLoginAttempts); Assert.Null(account.LockoutEnd);
        Assert.Equal("Updated profile name", result.User.Name);
        _repository.Verify(x => x.AddRefreshToken(It.Is<RefreshToken>(token => token.CreatedByIp == "127.0.0.7"
            && token.TokenHash == "hashed-refresh-token" && token.JwtId == "jwt-id")), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_UnknownUser_DoesNotWriteOrHash()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateService().LoginAsync(new("unknown", "wrong"), null, default));
        _passwordHasher.VerifyNoOtherCalls();
        _repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("revoked")]
    public async Task RefreshTokenAsync_InvalidToken_CannotIssueAnotherSession(string state)
    {
        ConfigureTokenService();
        if (state != "missing") _repository.Setup(x => x.GetRefreshTokenWithAccountAsync("hashed-refresh-token", It.IsAny<CancellationToken>())).ReturnsAsync(new RefreshToken
        {
            Account = CreateAccount(), ExpiresAt = DateTime.UtcNow.AddMinutes(state == "expired" ? -1 : 1),
            RevokedAt = state == "revoked" ? DateTime.UtcNow : null
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateService().RefreshTokenAsync(new("refresh-token"), null, default));
        _repository.Verify(x => x.AddRefreshToken(It.IsAny<RefreshToken>()), Times.Never);
        _repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("INACTIVE")]
    [InlineData("LOCKED")]
    public async Task RefreshTokenAsync_NonActiveAccount_CannotBypassStatusValidation(string status)
    {
        ConfigureTokenService(); var account = CreateAccount(); account.AccountStatus = status;
        var token = new RefreshToken { Account = account, ExpiresAt = DateTime.UtcNow.AddHours(1) };
        _repository.Setup(x => x.GetRefreshTokenWithAccountAsync("hashed-refresh-token", It.IsAny<CancellationToken>())).ReturnsAsync(token);
        await Assert.ThrowsAsync<SAW.Application.Exceptions.ForbiddenException>(() => CreateService().RefreshTokenAsync(new("refresh-token"), null, default));
        Assert.Null(token.RevokedAt);
        _repository.Verify(x => x.AddRefreshToken(It.IsAny<RefreshToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshTokenAsync_RotatesTokenAndReturnsPersistedNameAndRole_AndRejectsReplay()
    {
        ConfigureTokenService();
        _tokenService.Setup(x => x.HashToken("old-token")).Returns("old-hash");
        var account = CreateAccount(); account.FullName = "New profile name";
        var old = new RefreshToken { AccountId = 1, Account = account, TokenHash = "old-hash", ExpiresAt = DateTime.UtcNow.AddHours(1) };
        _repository.Setup(x => x.GetRefreshTokenWithAccountAsync("old-hash", It.IsAny<CancellationToken>())).ReturnsAsync(old);
        var result = await CreateService().RefreshTokenAsync(new("old-token"), "127.0.0.8", default);
        Assert.Equal("New profile name", result.User.Name); Assert.Equal("ADMINISTRATOR", result.User.Role);
        Assert.NotNull(old.RevokedAt); Assert.Equal("127.0.0.8", old.RevokedByIp);
        Assert.Equal("hashed-refresh-token", old.ReplacedByTokenHash);
        _repository.Verify(x => x.AddRefreshToken(It.Is<RefreshToken>(t => t.AccountId == 1 && t.TokenHash == old.ReplacedByTokenHash
            && t.CreatedByIp == "127.0.0.8" && t.ExpiresAt == result.RefreshTokenExpiresAt)), Times.Once);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateService().RefreshTokenAsync(new("old-token"), null, default));
        _repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LogoutAsync_MissingOrAlreadyRevokedToken_IsIdempotent(bool missing)
    {
        ConfigureTokenService();
        if (!missing) _repository.Setup(x => x.GetRefreshTokenAsync("hashed-refresh-token", It.IsAny<CancellationToken>())).ReturnsAsync(new RefreshToken { RevokedAt = DateTime.UtcNow });
        await CreateService().LogoutAsync(new("refresh-token"), null, default);
        _repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LogoutAsync_RevokesOnlyTheProvidedToken()
    {
        ConfigureTokenService(); var token = new RefreshToken { AccountId = 1 };
        _repository.Setup(x => x.GetRefreshTokenAsync("hashed-refresh-token", It.IsAny<CancellationToken>())).ReturnsAsync(token);
        await CreateService().LogoutAsync(new("refresh-token"), "127.0.0.9", default);
        Assert.NotNull(token.RevokedAt); Assert.Equal("127.0.0.9", token.RevokedByIp);
        _repository.Verify(x => x.GetActiveRefreshTokensAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private static RegisterRequest Registration(int roleId) => new(" new_user ", " NEW@SAW.LOCAL ", "GoodPass1!", " Full Name ", " 0901234567 ", roleId, " Company ", " TAX-NEW ", " Address ");

    [Theory]
    [InlineData(5, "SUPPLIER")]
    [InlineData(6, "DISTRIBUTOR")]
    public async Task RegisterAsync_CreatesPendingAccountAndBusinessProfile_AndSendsHashedVerification(int roleId, string roleCode)
    {
        ConfigureTokenService();
        _repository.Setup(x => x.GetActiveRoleAsync(roleId, It.IsAny<CancellationToken>())).ReturnsAsync(new Role { RoleId = roleId, RoleCode = roleCode });
        Account? created = null; Supplier? supplier = null; Distributor? distributor = null;
        _repository.Setup(x => x.CreateRegistrationAsync(It.IsAny<Account>(), It.IsAny<Supplier?>(), It.IsAny<Distributor?>(), It.IsAny<CancellationToken>()))
            .Callback<Account, Supplier?, Distributor?, CancellationToken>((a, s, d, _) => { a.AccountId = 7; created = a; supplier = s; distributor = d; });
        _passwordHasher.Setup(x => x.Hash("GoodPass1!")).Returns("stored-hash");
        var result = await CreateService().RegisterAsync(Registration(roleId), default);
        Assert.Equal(7, result.AccountId); Assert.Equal("new_user", result.Username); Assert.Equal("new@saw.local", result.Email);
        Assert.Equal("PENDING", created!.AccountStatus); Assert.Equal("Full Name", created.FullName); Assert.Equal("stored-hash", created.PasswordHash);
        Assert.Equal("0901234567", created.PhoneNumber);
        if (roleCode == "SUPPLIER") { Assert.Null(distributor); Assert.Equal("Company", supplier!.SupplierName); Assert.Equal("TAX-NEW", supplier.TaxCode); Assert.Equal("Address", supplier.Address); }
        else { Assert.Null(supplier); Assert.Equal("Company", distributor!.DistributorName); Assert.Equal("TAX-NEW", distributor.TaxCode); Assert.Equal("Address", distributor.Address); }
        _repository.Verify(x => x.AddEmailVerificationToken(It.Is<EmailVerificationToken>(t => t.AccountId == 7 && t.TokenHash == "hashed-refresh-token" && t.ExpiresAt == t.CreatedAt.AddHours(24))), Times.Once);
        _emailSender.Verify(x => x.SendEmailVerificationAsync("new@saw.local", "refresh-token", It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.AddRefreshToken(It.IsAny<RefreshToken>()), Times.Never);
    }

    [Theory]
    [InlineData("username")]
    [InlineData("email")]
    [InlineData("tax")]
    public async Task RegisterAsync_DuplicateIdentity_CannotCreateAccountOrSendMail(string duplicate)
    {
        if (duplicate == "username") _repository.Setup(x => x.UsernameExistsAsync("new_user", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        if (duplicate == "email") _repository.Setup(x => x.EmailExistsAsync("new@saw.local", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        if (duplicate == "tax") _repository.Setup(x => x.TaxCodeExistsAsync("TAX-NEW", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await Assert.ThrowsAsync<SAW.Application.Exceptions.ConflictException>(() => CreateService().RegisterAsync(Registration(5), default));
        _repository.Verify(x => x.CreateRegistrationAsync(It.IsAny<Account>(), It.IsAny<Supplier?>(), It.IsAny<Distributor?>(), It.IsAny<CancellationToken>()), Times.Never);
        _emailSender.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("ADMINISTRATOR")]
    [InlineData("WAREHOUSE_MANAGER")]
    [InlineData("QC_STAFF")]
    [InlineData("OPERATION_STAFF")]
    public async Task SelfRegistration_LocalAndGoogle_CannotSelectInternalRoles(string role)
    {
        _repository.Setup(x => x.GetActiveRoleAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new Role { RoleId = 1, RoleCode = role });
        _googleIdentityService.Setup(x => x.VerifyAsync("google-token", It.IsAny<CancellationToken>())).ReturnsAsync(new GoogleIdentity("subject", "new@saw.local", "New"));
        await Assert.ThrowsAsync<SAW.Application.Exceptions.BadRequestException>(() => CreateService().RegisterAsync(Registration(1), default));
        await Assert.ThrowsAsync<SAW.Application.Exceptions.BadRequestException>(() => CreateService().GoogleLoginAsync(new("google-token", 1), null, default));
        _repository.Verify(x => x.CreateRegistrationAsync(It.IsAny<Account>(), It.IsAny<Supplier?>(), It.IsAny<Distributor?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_InactiveOrUnknownRole_IsRejected()
    {
        await Assert.ThrowsAsync<SAW.Application.Exceptions.BadRequestException>(() => CreateService().RegisterAsync(Registration(99), default));
        _repository.Verify(x => x.CreateRegistrationAsync(It.IsAny<Account>(), It.IsAny<Supplier?>(), It.IsAny<Distributor?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("used")]
    public async Task VerifyEmailAsync_InvalidLink_DoesNotActivateAccount(string state)
    {
        ConfigureTokenService(); var account = CreateAccount(); account.AccountStatus = "PENDING";
        if (state != "missing") _repository.Setup(x => x.GetEmailVerificationTokenAsync("hashed-refresh-token", It.IsAny<CancellationToken>())).ReturnsAsync(new EmailVerificationToken
        {
            Account = account, ExpiresAt = DateTime.UtcNow.AddHours(state == "expired" ? -1 : 1), UsedAt = state == "used" ? DateTime.UtcNow : null
        });
        await Assert.ThrowsAsync<SAW.Application.Exceptions.BadRequestException>(() => CreateService().VerifyEmailAsync(new("refresh-token"), default));
        Assert.Equal("PENDING", account.AccountStatus);
        _repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyEmailAsync_ActivatesPendingAccountOnce_AndRepeatedUseDoesNotWrite()
    {
        ConfigureTokenService(); var account = CreateAccount(); account.AccountStatus = "PENDING";
        var token = new EmailVerificationToken { Account = account, ExpiresAt = DateTime.UtcNow.AddHours(1) };
        _repository.Setup(x => x.GetEmailVerificationTokenAsync("hashed-refresh-token", It.IsAny<CancellationToken>())).ReturnsAsync(token);
        await CreateService().VerifyEmailAsync(new("refresh-token"), default);
        Assert.Equal("ACTIVE", account.AccountStatus); Assert.NotNull(token.UsedAt); Assert.Equal(token.UsedAt, account.UpdatedAt);
        await CreateService().VerifyEmailAsync(new("refresh-token"), default);
        _repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.AddRefreshToken(It.IsAny<RefreshToken>()), Times.Never);
    }

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
