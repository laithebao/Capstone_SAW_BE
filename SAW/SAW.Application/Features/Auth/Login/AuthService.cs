using SAW.Application.Abstractions.Authentication;
using SAW.Application.Abstractions.Persistence;
using SAW.Application.Exceptions;

namespace SAW.Application.Features.Auth.Login;

public sealed class AuthService : IAuthService
{
    private const int MaxFailedLoginAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly IAccountRepository _accounts;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAccessTokenGenerator _tokenGenerator;

    public AuthService(IAccountRepository accounts, IPasswordHasher passwordHasher, IAccessTokenGenerator tokenGenerator)
    {
        _accounts = accounts;
        _passwordHasher = passwordHasher;
        _tokenGenerator = tokenGenerator;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var identifier = request.Identifier.Trim().ToLowerInvariant();
        var account = await _accounts.FindByIdentifierAsync(identifier, cancellationToken);

        if (account is null)
            throw new UnauthorizedAccessException("Email hoặc mật khẩu không đúng.");

        var now = DateTime.UtcNow;
        if (account.LockoutEnd is not null && account.LockoutEnd > now)
            throw new UnauthorizedAccessException("Tài khoản đang tạm khóa. Vui lòng thử lại sau.");

        if (!_passwordHasher.Verify(request.Password, account.PasswordHash))
        {
            account.FailedLoginAttempts++;
            if (account.FailedLoginAttempts >= MaxFailedLoginAttempts)
                account.LockoutEnd = now.Add(LockoutDuration);

            await _accounts.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedAccessException("Email hoặc mật khẩu không đúng.");
        }

        if (!string.Equals(account.AccountStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            throw new ForbiddenException("Tài khoản chưa được kích hoạt hoặc đã bị vô hiệu hóa.");

        if (!account.Role.IsActive)
            throw new ForbiddenException("Vai trò của tài khoản đã bị vô hiệu hóa.");

        account.FailedLoginAttempts = 0;
        account.LockoutEnd = null;
        account.LastLoginAt = now;

        var token = _tokenGenerator.Generate(account);
        await _accounts.SaveChangesAsync(cancellationToken);

        return new LoginResponse(
            token.Token,
            token.ExpiresAt,
            new AuthenticatedUser(account.AccountId, account.FullName, account.Email, account.Role.RoleCode));
    }
}
