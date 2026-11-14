using SAW.Domain.Entities;

namespace SAW.Application.Features.UserProfiles;

public sealed record UserProfileResponse(
    int AccountId, string Username, string Email, string FullName, string? PhoneNumber,
    string? AvatarUrl, string RoleCode, DistributorProfileResponse? Distributor);

public sealed record DistributorProfileResponse(
    int DistributorId, string DistributorCode, string DistributorName, string TaxCode,
    string? ContactPerson, string? PhoneNumber, string? Email, string? Address);

public sealed record UpdateUserProfileRequest(
    string FullName, string? PhoneNumber, UpdateDistributorProfileRequest? Distributor = null);

public sealed record UpdateDistributorProfileRequest(
    string DistributorName, string? TaxCode, string? ContactPerson,
    string? PhoneNumber, string? Email, string? Address);

public interface IUserProfileService
{
    Task<UserProfileResponse> GetAsync(int accountId, CancellationToken cancellationToken);
    Task<UserProfileResponse> UpdateAsync(int accountId, UpdateUserProfileRequest request, CancellationToken cancellationToken);
}

public interface IUserProfileRepository
{
    Task<Account?> GetByAccountIdAsync(int accountId, CancellationToken cancellationToken);
    Task<bool> TaxCodeExistsAsync(string taxCode, int excludingDistributorId, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
