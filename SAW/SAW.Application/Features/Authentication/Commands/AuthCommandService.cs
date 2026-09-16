using SAW.Application.Exceptions;
using SAW.Application.Features.Authentication.DTOs;
using SAW.Application.Repositories;
using SAW.Domain.Entities;

namespace SAW.Application.Features.Authentication.Commands;

public sealed class AuthCommandService(
    IAuthRepository repository,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    IEmailSender emailSender,
    IGoogleIdentityService googleIdentityService) : IAuthCommandService
{
    private const int MaximumFailedLogins = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public async Task<AuthResponse> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken cancellationToken)
    {
        var account = await repository.GetAccountByIdentifierAsync(request.Identifier.Trim(), cancellationToken);
        if (account is null) throw new UnauthorizedAccessException("Incorrect user name or password. Please check again.");

        var now = DateTime.UtcNow;
        if (account.AccountStatus == "INACTIVE") throw new ForbiddenException("Your account has been disabled.");
        if (account.AccountStatus == "PENDING") throw new ForbiddenException("Your account is pending activation.");
        if (account.AccountStatus == "LOCKED" && account.LockoutEnd is null) throw new ForbiddenException("Your account has been locked.");
        if (account.LockoutEnd > now) throw new ForbiddenException($"Your account is temporarily locked until {account.LockoutEnd:O}.");

        if (!passwordHasher.Verify(request.Password, account.PasswordHash))
        {
            account.FailedLoginAttempts++;
            if (account.FailedLoginAttempts >= MaximumFailedLogins)
            {
                account.LockoutEnd = now.Add(LockoutDuration);
                account.FailedLoginAttempts = 0;
            }
            account.UpdatedAt = now;
            await repository.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedAccessException("Incorrect user name or password. Please check again.");
        }

        account.FailedLoginAttempts = 0;
        account.LockoutEnd = null;
        account.LastLoginAt = now;
        account.UpdatedAt = now;

        var accessToken = tokenService.CreateAccessToken(
            account.AccountId, account.Username, account.Email, account.RoleId, account.Role.RoleCode);
        var rawRefreshToken = tokenService.CreateOpaqueToken();
        var refreshTokenExpiresAt = now.AddDays(tokenService.RefreshTokenDays);
        repository.AddRefreshToken(new RefreshToken
        {
            AccountId = account.AccountId,
            TokenHash = tokenService.HashToken(rawRefreshToken),
            JwtId = accessToken.JwtId,
            CreatedAt = now,
            ExpiresAt = refreshTokenExpiresAt,
            CreatedByIp = ipAddress
        });
        await repository.SaveChangesAsync(cancellationToken);

        return CreateAuthResponse(account, accessToken, rawRefreshToken, refreshTokenExpiresAt);
    }

    public async Task<GoogleLoginResponse> GoogleLoginAsync(
        GoogleLoginRequest request, string? ipAddress, CancellationToken cancellationToken)
    {
        var identity = await googleIdentityService.VerifyAsync(request.IdToken, cancellationToken);
        var account = await repository.GetAccountByEmailAsync(identity.Email, cancellationToken);

        if (account is null)
        {
            if (request.RoleId is null) return new GoogleLoginResponse(true, null);
            var role = await repository.GetActiveRoleAsync(request.RoleId.Value, cancellationToken)
                ?? throw new BadRequestException("Selected role is invalid or inactive.");
            if (role.RoleCode is not ("SUPPLIER" or "DISTRIBUTOR"))
                throw new BadRequestException("Only Supplier and Distributor roles can be selected.");

            var now = DateTime.UtcNow;
            var suffix = identity.Subject.Length <= 24 ? identity.Subject : identity.Subject[^24..];
            account = new Account
            {
                RoleId = role.RoleId,
                Username = $"google_{suffix}",
                Email = identity.Email,
                PasswordHash = passwordHasher.Hash(tokenService.CreateOpaqueToken()),
                FullName = identity.FullName,
                AccountStatus = "ACTIVE",
                CreatedAt = now
            };
            var placeholderTaxCode = $"GOOGLE-{suffix}";
            Supplier? supplier = role.RoleCode == "SUPPLIER" ? new Supplier
            {
                SupplierName = identity.FullName,
                TaxCode = placeholderTaxCode,
                ContactPerson = identity.FullName,
                Email = identity.Email,
                Address = string.Empty,
                ProfileStatus = "PENDING_REVIEW",
                CreatedAt = now
            } : null;
            Distributor? distributor = role.RoleCode == "DISTRIBUTOR" ? new Distributor
            {
                DistributorName = identity.FullName,
                TaxCode = placeholderTaxCode,
                ContactPerson = identity.FullName,
                Email = identity.Email,
                ProfileStatus = "PENDING_REVIEW",
                CreatedAt = now
            } : null;
            await repository.CreateRegistrationAsync(account, supplier, distributor, cancellationToken);
        }

        if (account.AccountStatus == "INACTIVE" || account.AccountStatus == "LOCKED")
            throw new ForbiddenException("Your account is not allowed to sign in.");
        if (account.AccountStatus == "PENDING") account.AccountStatus = "ACTIVE";

        var session = await CreateSessionAsync(account, ipAddress, cancellationToken);
        return new GoogleLoginResponse(false, session);
    }

    public async Task<AuthResponse> RefreshTokenAsync(
        RefreshTokenRequest request, string? ipAddress, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var currentToken = await repository.GetRefreshTokenWithAccountAsync(
            tokenService.HashToken(request.RefreshToken), cancellationToken);
        if (currentToken is null || currentToken.RevokedAt is not null || currentToken.ExpiresAt <= now)
            throw new UnauthorizedAccessException("Refresh token is invalid, expired, or revoked.");
        if (currentToken.Account.AccountStatus != "ACTIVE")
            throw new ForbiddenException("Your account is not active.");

        var accessToken = tokenService.CreateAccessToken(
            currentToken.AccountId, currentToken.Account.Username, currentToken.Account.Email,
            currentToken.Account.RoleId, currentToken.Account.Role.RoleCode);
        var rawRefreshToken = tokenService.CreateOpaqueToken();
        var refreshTokenExpiresAt = now.AddDays(tokenService.RefreshTokenDays);

        currentToken.RevokedAt = now;
        currentToken.RevokedByIp = ipAddress;
        currentToken.ReplacedByTokenHash = tokenService.HashToken(rawRefreshToken);
        repository.AddRefreshToken(new RefreshToken
        {
            AccountId = currentToken.AccountId,
            TokenHash = currentToken.ReplacedByTokenHash,
            JwtId = accessToken.JwtId,
            CreatedAt = now,
            ExpiresAt = refreshTokenExpiresAt,
            CreatedByIp = ipAddress
        });
        await repository.SaveChangesAsync(cancellationToken);

        var account = currentToken.Account;
        return CreateAuthResponse(account, accessToken, rawRefreshToken, refreshTokenExpiresAt);
    }

    public async Task<RegistrationResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        ValidatePassword(request.Password);
        var username = request.Username.Trim();
        var email = request.Email.Trim().ToLowerInvariant();
        var taxCode = request.TaxCode.Trim();
        if (await repository.UsernameExistsAsync(username, cancellationToken)) throw new ConflictException("Username already exists.");
        if (await repository.EmailExistsAsync(email, cancellationToken)) throw new ConflictException("Email already exists.");
        if (await repository.TaxCodeExistsAsync(taxCode, cancellationToken)) throw new ConflictException("Tax code already exists.");

        var role = await repository.GetActiveRoleAsync(request.RoleId, cancellationToken)
            ?? throw new BadRequestException($"Role ID '{request.RoleId}' is not configured or inactive.");
        if (role.RoleCode is not ("SUPPLIER" or "DISTRIBUTOR"))
            throw new BadRequestException("Only Supplier and Distributor accounts can self-register.");
        var now = DateTime.UtcNow;
        var account = new Account
        {
            RoleId = role.RoleId, Username = username, Email = email,
            PasswordHash = passwordHasher.Hash(request.Password), FullName = request.FullName.Trim(),
            PhoneNumber = request.PhoneNumber?.Trim(), AccountStatus = "PENDING", CreatedAt = now
        };

        Supplier? supplier = null;
        Distributor? distributor = null;
        if (role.RoleCode == "SUPPLIER")
        {
            supplier = new Supplier
            {
                SupplierName = request.OrganizationName.Trim(), TaxCode = taxCode,
                ContactPerson = account.FullName, PhoneNumber = account.PhoneNumber, Email = email,
                Address = request.Address?.Trim() ?? string.Empty, CreatedAt = now
            };
        }
        else
        {
            distributor = new Distributor
            {
                DistributorName = request.OrganizationName.Trim(), TaxCode = taxCode,
                ContactPerson = account.FullName, PhoneNumber = account.PhoneNumber, Email = email,
                Address = request.Address?.Trim(), CreatedAt = now
            };
        }

        await repository.CreateRegistrationAsync(account, supplier, distributor, cancellationToken);
        var rawVerificationToken = tokenService.CreateOpaqueToken();
        repository.AddEmailVerificationToken(new EmailVerificationToken
        {
            AccountId = account.AccountId,
            TokenHash = tokenService.HashToken(rawVerificationToken),
            CreatedAt = now,
            ExpiresAt = now.AddHours(24)
        });
        await repository.SaveChangesAsync(cancellationToken);
        await emailSender.SendEmailVerificationAsync(account.Email, rawVerificationToken, cancellationToken);
        return new RegistrationResponse(account.AccountId, account.Username, account.Email, account.RoleId);
    }

    public async Task VerifyEmailAsync(VerifyEmailRequest request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var verificationToken = await repository.GetEmailVerificationTokenAsync(
            tokenService.HashToken(request.Token), cancellationToken);
        if (verificationToken is null || verificationToken.ExpiresAt <= now)
            throw new BadRequestException("Verification link is invalid or has expired.");
        if (verificationToken.UsedAt is not null)
        {
            if (verificationToken.Account.AccountStatus == "ACTIVE") return;
            throw new BadRequestException("Verification link has already been used.");
        }

        verificationToken.UsedAt = now;
        verificationToken.Account.AccountStatus = "ACTIVE";
        verificationToken.Account.UpdatedAt = now;
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task LogoutAsync(LogoutRequest request, string? ipAddress, CancellationToken cancellationToken)
    {
        var token = await repository.GetRefreshTokenAsync(tokenService.HashToken(request.RefreshToken), cancellationToken);
        if (token is null || token.RevokedAt is not null) return;
        token.RevokedAt = DateTime.UtcNow;
        token.RevokedByIp = ipAddress;
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task RequestPasswordResetAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var account = await repository.GetAccountByIdentifierAsync(request.Email.Trim().ToLowerInvariant(), cancellationToken);
        if (account is null) return;

        var now = DateTime.UtcNow;
        foreach (var token in await repository.GetActivePasswordResetTokensAsync(account.AccountId, now, cancellationToken))
            token.UsedAt = now;

        var rawToken = tokenService.CreateOpaqueToken();
        repository.AddPasswordResetToken(new PasswordResetToken
        {
            AccountId = account.AccountId, TokenHash = tokenService.HashToken(rawToken),
            CreatedAt = now, ExpiresAt = now.AddHours(24)
        });
        await repository.SaveChangesAsync(cancellationToken);
        await emailSender.SendPasswordResetAsync(account.Email, rawToken, cancellationToken);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        ValidatePassword(request.NewPassword);
        var now = DateTime.UtcNow;
        var resetToken = await repository.GetPasswordResetTokenAsync(tokenService.HashToken(request.Token), cancellationToken);
        if (resetToken is null || resetToken.UsedAt is not null || resetToken.ExpiresAt <= now)
            throw new BadRequestException("Reset link is invalid or has expired.");

        resetToken.Account.PasswordHash = passwordHasher.Hash(request.NewPassword);
        resetToken.Account.UpdatedAt = now;
        resetToken.UsedAt = now;
        await RevokeAllRefreshTokensAsync(resetToken.AccountId, now, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task ChangePasswordAsync(int accountId, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        ValidatePassword(request.NewPassword);
        var account = await repository.GetAccountByIdAsync(accountId, cancellationToken)
            ?? throw new NotFoundException("Account not found.");
        if (!passwordHasher.Verify(request.CurrentPassword, account.PasswordHash))
            throw new BadRequestException("Current password is incorrect.");
        if (passwordHasher.Verify(request.NewPassword, account.PasswordHash))
            throw new BadRequestException("New password must be different from current password.");

        var now = DateTime.UtcNow;
        account.PasswordHash = passwordHasher.Hash(request.NewPassword);
        account.UpdatedAt = now;
        await RevokeAllRefreshTokensAsync(accountId, now, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
    }

    private async Task RevokeAllRefreshTokensAsync(int accountId, DateTime revokedAt, CancellationToken cancellationToken)
    {
        foreach (var token in await repository.GetActiveRefreshTokensAsync(accountId, cancellationToken))
            token.RevokedAt = revokedAt;
    }

    private async Task<AuthResponse> CreateSessionAsync(
        Account account, string? ipAddress, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        account.LastLoginAt = now;
        account.UpdatedAt = now;
        var accessToken = tokenService.CreateAccessToken(
            account.AccountId, account.Username, account.Email, account.RoleId, account.Role.RoleCode);
        var rawRefreshToken = tokenService.CreateOpaqueToken();
        var refreshTokenExpiresAt = now.AddDays(tokenService.RefreshTokenDays);
        repository.AddRefreshToken(new RefreshToken
        {
            AccountId = account.AccountId,
            TokenHash = tokenService.HashToken(rawRefreshToken),
            JwtId = accessToken.JwtId,
            CreatedAt = now,
            ExpiresAt = refreshTokenExpiresAt,
            CreatedByIp = ipAddress
        });
        await repository.SaveChangesAsync(cancellationToken);
        return CreateAuthResponse(account, accessToken, rawRefreshToken, refreshTokenExpiresAt);
    }

    private static AuthResponse CreateAuthResponse(
        Account account,
        AccessTokenResult accessToken,
        string refreshToken,
        DateTime refreshTokenExpiresAt) =>
        new(
            new AuthenticatedUser(account.AccountId, account.FullName, account.Email, account.Role.RoleCode),
            accessToken.Token,
            accessToken.ExpiresAt,
            refreshToken,
            refreshTokenExpiresAt);

    private static void ValidatePassword(string password)
    {
        var valid = password.Length >= 8 && password.Any(char.IsUpper) && password.Any(char.IsLower)
            && password.Any(char.IsDigit) && password.Any(ch => !char.IsLetterOrDigit(ch));
        if (!valid)
            throw new BadRequestException("Password must contain at least 8 characters, including uppercase, lowercase, number, and special character.");
    }
}
