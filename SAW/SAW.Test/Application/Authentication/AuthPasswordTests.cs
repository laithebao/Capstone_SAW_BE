using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.Authentication.Commands;
using SAW.Application.Features.Authentication.DTOs;
using SAW.Application.Repositories;
using SAW.Domain.Entities;

namespace SAW.Test.Application.Authentication;

public sealed class AuthPasswordTests
{
    private readonly Mock<IAuthRepository> repository = new();
    private readonly Mock<IPasswordHasher> hasher = new();
    private readonly Mock<ITokenService> tokens = new();
    private readonly Mock<IEmailSender> email = new();
    private readonly Account account = new() { AccountId = 7, Email = "staff@saw.local", PasswordHash = "old-hash" };
    private readonly RefreshToken[] refreshTokens = [new() { AccountId = 7 }, new() { AccountId = 7 }];
    private AuthCommandService Service => new(repository.Object, hasher.Object, tokens.Object, email.Object, Mock.Of<IGoogleIdentityService>());

    public AuthPasswordTests()
    {
        repository.Setup(x => x.GetAccountByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        repository.Setup(x => x.GetActiveRefreshTokensAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(refreshTokens);
        hasher.Setup(x => x.Verify("OldPass1!", "old-hash")).Returns(true);
        hasher.Setup(x => x.Hash("NewPass2!")).Returns("new-hash");
        tokens.Setup(x => x.HashToken("reset-token")).Returns("reset-hash");
    }

    [Fact]
    public async Task ChangePassword_UsesAuthenticatedAccount_AndRevokesEveryRefreshToken()
    {
        await Service.ChangePasswordAsync(7, new("OldPass1!", "NewPass2!"), default);
        Assert.Equal("new-hash", account.PasswordHash);
        Assert.All(refreshTokens, token => Assert.Equal(account.UpdatedAt, token.RevokedAt));
        Assert.NotNull(account.UpdatedAt);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("short")][InlineData("alllower1!")][InlineData("ALLUPPER1!")][InlineData("NoDigits!!")][InlineData("NoSpecial1")]
    public async Task WeakPasswords_AreRejectedForChangeAndResetWithoutSaving(string password)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Service.ChangePasswordAsync(7, new("OldPass1!", password), default));
        await Assert.ThrowsAsync<BadRequestException>(() => Service.ResetPasswordAsync(new("reset-token", password), default));
        Assert.Equal("old-hash", account.PasswordHash);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WrongCurrentOrUnchangedPassword_DoesNotChangeHashOrRevokeSessions()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Service.ChangePasswordAsync(7, new("WrongPass1!", "NewPass2!"), default));
        await Assert.ThrowsAsync<BadRequestException>(() => Service.ChangePasswordAsync(7, new("OldPass1!", "OldPass1!"), default));
        Assert.Equal("old-hash", account.PasswordHash);
        Assert.All(refreshTokens, token => Assert.Null(token.RevokedAt));
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MissingAccount_CannotChangePassword() =>
        await Assert.ThrowsAsync<NotFoundException>(() => Service.ChangePasswordAsync(99, new("OldPass1!", "NewPass2!"), default));

    [Theory]
    [InlineData("missing")][InlineData("expired")][InlineData("used")]
    public async Task InvalidResetToken_CannotChangePassword(string state)
    {
        if (state != "missing") repository.Setup(x => x.GetPasswordResetTokenAsync("reset-hash", It.IsAny<CancellationToken>())).ReturnsAsync(new PasswordResetToken
        {
            AccountId = 7, Account = account, ExpiresAt = DateTime.UtcNow.AddHours(state == "expired" ? -1 : 1),
            UsedAt = state == "used" ? DateTime.UtcNow : null
        });
        await Assert.ThrowsAsync<BadRequestException>(() => Service.ResetPasswordAsync(new("reset-token", "NewPass2!"), default));
        Assert.Equal("old-hash", account.PasswordHash);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResetPassword_ConsumesTokenAndRevokesAllSessions()
    {
        var reset = new PasswordResetToken { AccountId = 7, Account = account, ExpiresAt = DateTime.UtcNow.AddHours(1) };
        repository.Setup(x => x.GetPasswordResetTokenAsync("reset-hash", It.IsAny<CancellationToken>())).ReturnsAsync(reset);
        await Service.ResetPasswordAsync(new("reset-token", "NewPass2!"), default);
        Assert.Equal("new-hash", account.PasswordHash);
        Assert.NotNull(reset.UsedAt);
        Assert.Equal(account.UpdatedAt, reset.UsedAt);
        Assert.All(refreshTokens, token => Assert.Equal(reset.UsedAt, token.RevokedAt));
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ForgotPassword_UnknownEmailDoesNotSendMailOrWrite()
    {
        await Service.RequestPasswordResetAsync(new(" unknown@saw.local "), default);
        email.VerifyNoOtherCalls();
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ForgotPassword_NormalizesEmail_ConsumesOldLinks_AndStoresOnlyTokenHash()
    {
        var previous = new PasswordResetToken { AccountId = 7 };
        repository.Setup(x => x.GetAccountByIdentifierAsync("staff@saw.local", It.IsAny<CancellationToken>())).ReturnsAsync(account);
        repository.Setup(x => x.GetActivePasswordResetTokensAsync(7, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { previous });
        tokens.Setup(x => x.CreateOpaqueToken()).Returns("reset-token");
        await Service.RequestPasswordResetAsync(new(" STAFF@SAW.LOCAL "), default);
        Assert.NotNull(previous.UsedAt);
        repository.Verify(x => x.AddPasswordResetToken(It.Is<PasswordResetToken>(t => t.AccountId == 7 && t.TokenHash == "reset-hash" && t.ExpiresAt == t.CreatedAt.AddHours(24))), Times.Once);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        email.Verify(x => x.SendPasswordResetAsync(account.Email, "reset-token", It.IsAny<CancellationToken>()), Times.Once);
    }
}
