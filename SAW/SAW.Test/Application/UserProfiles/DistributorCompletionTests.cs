using Moq;
using SAW.Application.Features.UserProfiles;
using SAW.Application.Exceptions;
using SAW.Domain.Entities;
using SAW.Infrastructure.Notifications;

namespace SAW.Test.Application.UserProfiles;
public sealed class DistributorCompletionTests
{
    private static Account Account(string tax = "TAX-REAL", string status = "ACTIVE") => new() {
        AccountId = 42, FullName = "Distributor owner", Email = "owner@example.invalid", Role = new Role { RoleCode = "DISTRIBUTOR" },
        Distributor = new Distributor { DistributorId = 9, DistributorCode = "DST-42", DistributorName = "Company", TaxCode = tax, ProfileStatus = status }
    };
    [Fact]
    public void CompletionUsesRequiredAndOptionalValidation_NotRecordPresence()
    {
        var a = Account(); Assert.True(DistributorProfileCompletion.IsComplete(a));
        a.Distributor!.PhoneNumber = "123"; Assert.False(DistributorProfileCompletion.IsComplete(a));
        a.Distributor.PhoneNumber = null; a.Distributor.DistributorName = " "; Assert.False(DistributorProfileCompletion.IsComplete(a));
        Assert.False(DistributorProfileCompletion.IsComplete(Account("GOOGLE-generated")));
        Assert.False(DistributorProfileCompletion.IsComplete(Account(status: "PENDING_REVIEW")));
        Assert.False(DistributorProfileCompletion.IsComplete(Account(status: "SUSPENDED")));
        a.Distributor = null; Assert.False(DistributorProfileCompletion.IsComplete(a));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task MissingOrGeneratedProfileCanBeCompletedWithoutNewFields(bool generated)
    {
        var a = Account("GOOGLE-generated", "PENDING_REVIEW"); if (!generated) a.Distributor = null;
        var repo = new Mock<IUserProfileRepository>(); repo.Setup(x => x.GetByAccountIdAsync(42, default)).ReturnsAsync(a);
        var result = await new UserProfileService(repo.Object).UpdateAsync(42, new("Owner", null, new("Company", "REAL-TAX", null, null, null, null)), default);
        Assert.True(result.IsProfileComplete); Assert.Equal("REAL-TAX", a.Distributor!.TaxCode);
        repo.Verify(x => x.SaveChangesAsync(default), Times.Once);
        await Assert.ThrowsAsync<ValidationException>(() => new UserProfileService(repo.Object).UpdateAsync(42, new("Owner", null, new("Company", "CHANGED", null, null, null, null)), default));
    }
    [Fact]
    public async Task InvalidCompletionDoesNotMutateOrSave()
    {
        var a = Account("GOOGLE-generated", "PENDING_REVIEW");
        var repo = new Mock<IUserProfileRepository>(); repo.Setup(x => x.GetByAccountIdAsync(42, default)).ReturnsAsync(a);
        await Assert.ThrowsAsync<ValidationException>(() => new UserProfileService(repo.Object).UpdateAsync(42, new("Owner", null, new("Company", "GOOGLE-generated", null, "wrong", null, null)), default));
        Assert.Equal("PENDING_REVIEW", a.Distributor!.ProfileStatus); repo.Verify(x => x.SaveChangesAsync(default), Times.Never);
    }
    [Fact]
    public void TrustedGoogleProofMatchesAccountAndCurrentEmail_ActiveAloneIsNotProof()
    {
        var a = Account(); a.AccountStatus = "ACTIVE"; var eligible = VerifiedEmail.Eligible.Compile();
        Assert.False(eligible(a));
        a.AuditLogs.Add(new AuditLog { ActionType = "EMAIL_VERIFIED_GOOGLE", EntityName = "ACCOUNT", Description = a.Email }); Assert.True(eligible(a));
        a.Email = "changed@example.invalid"; Assert.False(eligible(a));
        a.EmailVerificationTokens.Add(new EmailVerificationToken { UsedAt = DateTime.UtcNow }); Assert.True(eligible(a));
        a.AccountStatus = "INACTIVE"; Assert.False(eligible(a));
    }
}
