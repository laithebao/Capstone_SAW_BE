using System.Text.Json;
using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.UserProfiles;
using SAW.Domain.Entities;

namespace SAW.Test.Application.UserProfiles;

public sealed class UserProfileServiceTests
{
    private readonly Mock<IUserProfileRepository> repository = new();
    private readonly Account account = new()
    {
        AccountId = 7, Username = "staff", Email = "login@saw.local", FullName = "Original name",
        PhoneNumber = "0901111111", PasswordHash = "secret", RoleId = 3,
        Role = new Role { RoleId = 3, RoleCode = "QC_STAFF" }, AccountStatus = "ACTIVE",
        FailedLoginAttempts = 2, InactiveReviewFlag = true,
        Distributor = new Distributor
        {
            DistributorId = 8, AccountId = 7, DistributorCode = "DIS-000007", DistributorName = "Original company",
            TaxCode = "ORIGINAL", ProfileStatus = "SUSPENDED", HasOverdueBalance = true, OverdueNote = "Debt note"
        }
    };
    private UserProfileService Service => new(repository.Object);
    private static UpdateDistributorProfileRequest Business => new(" New company ", " ORIGINAL ", " Contact ", " +84 901 234 567 ", " contact@saw.local ", " New address ");
    private static UpdateUserProfileRequest Request => new(" New name ", " 0901234567 ");

    public UserProfileServiceTests()
    {
        repository.Setup(x => x.GetByAccountIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(account);
    }

    [Theory]
    [InlineData("ADMINISTRATOR")][InlineData("WAREHOUSE_MANAGER")][InlineData("QC_STAFF")][InlineData("OPERATION_STAFF")]
    public async Task StaffProfiles_ReturnOnlyPersonalInformation_AndGetDoesNotWrite(string role)
    {
        account.Role.RoleCode = role;
        var result = await Service.GetAsync(7, default);
        Assert.Equal(7, result.AccountId);
        Assert.Equal(role, result.RoleCode);
        Assert.Equal(account.Email, result.Email);
        Assert.Null(result.Distributor);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("PasswordHash", json);
        Assert.DoesNotContain("OverdueNote", json);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DistributorProfile_ReturnsBusinessDetailsWithoutSupplierFields()
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        var result = await Service.GetAsync(7, default);
        Assert.Equal("DIS-000007", result.Distributor!.DistributorCode);
        Assert.Equal("ORIGINAL", result.Distributor.TaxCode);
        Assert.DoesNotContain("GrowingArea", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("CropType", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("ADMINISTRATOR")][InlineData("WAREHOUSE_MANAGER")][InlineData("QC_STAFF")][InlineData("OPERATION_STAFF")]
    public async Task UpdateStaff_TrimsEditableFields_AndPreservesAuthenticationAndBusinessState(string role)
    {
        account.Role.RoleCode = role;
        var result = await Service.UpdateAsync(7, Request, default);
        Assert.Equal("New name", result.FullName);
        Assert.Equal("0901234567", result.PhoneNumber);
        Assert.Equal("login@saw.local", account.Email);
        Assert.Equal("staff", account.Username);
        Assert.Equal("secret", account.PasswordHash);
        Assert.Equal(3, account.RoleId);
        Assert.Equal(role, account.Role.RoleCode);
        Assert.Equal("ACTIVE", account.AccountStatus);
        Assert.Equal(2, account.FailedLoginAttempts);
        Assert.True(account.InactiveReviewFlag);
        Assert.Equal("Original company", account.Distributor!.DistributorName);
        Assert.NotNull(account.UpdatedAt);
        repository.Verify(x => x.TaxCodeExistsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateDistributor_SavesPersonalAndBusinessDetailsTogether_WithoutChangingDebtOrStatus()
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        var result = await Service.UpdateAsync(7, Request with { Distributor = Business }, default);
        Assert.Equal("New name", result.FullName);
        Assert.Equal("New company", result.Distributor!.DistributorName);
        Assert.Equal("ORIGINAL", result.Distributor.TaxCode);
        Assert.Equal("Contact", result.Distributor.ContactPerson);
        Assert.Equal("+84901234567", result.Distributor.PhoneNumber);
        Assert.Equal("contact@saw.local", result.Distributor.Email);
        Assert.Equal("New address", result.Distributor.Address);
        Assert.Equal("DIS-000007", result.Distributor.DistributorCode);
        Assert.True(account.Distributor!.HasOverdueBalance);
        Assert.Equal("Debt note", account.Distributor.OverdueNote);
        Assert.Equal("SUSPENDED", account.Distributor.ProfileStatus);
        Assert.Equal(account.UpdatedAt, account.Distributor.UpdatedAt);
        repository.Verify(x => x.TaxCodeExistsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)][InlineData("")][InlineData("   ")]
    public async Task OptionalContactFields_CanBeCleared(string? value)
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        var result = await Service.UpdateAsync(7, Request with { PhoneNumber = value, Distributor = Business with
        { ContactPerson = value, PhoneNumber = value, Email = value, Address = value } }, default);
        Assert.Null(result.PhoneNumber);
        Assert.Null(result.Distributor!.ContactPerson);
        Assert.Null(result.Distributor.PhoneNumber);
        Assert.Null(result.Distributor.Email);
        Assert.Null(result.Distributor.Address);
    }

    [Theory]
    [InlineData(null)][InlineData("")][InlineData("   ")]
    public async Task BlankFullName_IsRejectedWithoutMutation(string? name)
    {
        await AssertInvalid(Request with { FullName = name! });
    }

    [Fact]
    public async Task FullNameAndPhone_RespectDatabaseLimits()
    {
        await AssertInvalid(Request with { FullName = new string('a', 151) });
        await AssertInvalid(Request with { PhoneNumber = new string('1', 31) });
        await Service.UpdateAsync(7, Request with { FullName = new string('a', 150), PhoneNumber = "0901234567" }, default);
    }

    [Theory]
    [InlineData("abc")][InlineData("123")][InlineData("090<script>")][InlineData("++84901234567")]
    [InlineData("1234567890")][InlineData("090123456")][InlineData("09012345678")][InlineData("+840901234567")]
    [InlineData("+8490123456")][InlineData("(090)1234567")][InlineData("0.....")][InlineData("0901234567 ext 1")]
    public async Task InvalidPhone_IsRejectedWithoutMutation(string phone) => await AssertInvalid(Request with { PhoneNumber = phone });

    [Fact]
    public async Task ChangingTaxCode_DoesNotMutateEitherEntityOrSave()
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        var error = await Assert.ThrowsAsync<ValidationException>(() => Service.UpdateAsync(7, Request with { Distributor = Business with { TaxCode = "TAX-NEW" } }, default));
        Assert.Contains(error.Errors, message => message.Contains("Mã số thuế"));
        Assert.Equal("ORIGINAL", account.Distributor!.TaxCode);
        AssertUnchanged();
    }

    [Fact]
    public async Task DistributorRequiresBusinessDetails_AndStaffCannotSubmitBusinessFields()
    {
        await AssertInvalid(Request with { Distributor = Business });
        account.Role.RoleCode = "DISTRIBUTOR";
        await AssertInvalid(Request);
    }

    [Fact]
    public async Task InvalidBusinessFields_RejectBeforePersonalOrBusinessMutation()
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        foreach (var business in new[]
        {
            Business with { DistributorName = " " }, Business with { DistributorName = null! },
            Business with { DistributorName = new string('a', 201) }, Business with { TaxCode = " " },
            Business with { TaxCode = new string('1', 51) },
            Business with { ContactPerson = new string('a', 151) }, Business with { Address = new string('a', 501) },
            Business with { PhoneNumber = "invalid" }, Business with { PhoneNumber = new string('1', 31) },
            Business with { Email = "invalid email" }, Business with { Email = new string('a', 256) }
        }) await AssertInvalid(Request with { Distributor = business });
    }

    [Fact]
    public async Task BusinessFields_AcceptDatabaseMaximumLengthsAndUnchangedTaxCode()
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        var result = await Service.UpdateAsync(7, Request with { Distributor = Business with
        {
            DistributorName = new string('a', 200), TaxCode = "ORIGINAL", ContactPerson = new string('a', 150),
            Address = new string('a', 500), PhoneNumber = "0901234567"
        } }, default);
        Assert.Equal(200, result.Distributor!.DistributorName.Length);
        Assert.Equal("ORIGINAL", result.Distributor.TaxCode);
    }

    [Theory]
    [InlineData("0901234567", "0901234567")]
    [InlineData(" +84901234567 ", "+84901234567")]
    [InlineData("090 123 4567", "0901234567")]
    [InlineData("090.123.4567", "0901234567")]
    [InlineData("090-123-4567", "0901234567")]
    public async Task BothPhoneFieldsFollowRegistrationConventionAndStoreNormalizedNumber(string value, string expected)
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        var result = await Service.UpdateAsync(7, Request with { PhoneNumber = value, Distributor = Business with { PhoneNumber = value, TaxCode = null } }, default);
        Assert.Equal(expected, result.PhoneNumber); Assert.Equal(expected, result.Distributor!.PhoneNumber); Assert.Equal("ORIGINAL", result.Distributor.TaxCode);
    }

    [Theory]
    [InlineData("090123456")][InlineData("1234567890")][InlineData("+840901234567")][InlineData("0.....")]
    public async Task InvalidBusinessPhoneDoesNotPartiallySavePersonalChanges(string value)
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        await AssertInvalid(Request with { Distributor = Business with { PhoneNumber = value } });
    }

    [Theory]
    [InlineData("user@host")][InlineData("Name <contact@example.com>")][InlineData("a..b@example.com")]
    [InlineData("a@b@c.com")][InlineData("a b@example.com")][InlineData(".user@example.com")][InlineData("user@example.c")]
    public async Task InvalidContactEmailIsRejectedBeforeAnyMutation(string value)
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        await AssertInvalid(Request with { Distributor = Business with { Email = value } });
    }

    [Theory]
    [InlineData(" contact@example.com ", "contact@example.com")]
    [InlineData("first.last+orders@example.com", "first.last+orders@example.com")]
    public async Task ContactEmailAcceptsRegistrationFormatAndTrimsOuterWhitespace(string value, string expected)
    {
        account.Role.RoleCode = "DISTRIBUTOR";
        var result = await Service.UpdateAsync(7, Request with { Distributor = Business with { Email = value } }, default);
        Assert.Equal(expected, result.Distributor!.Email); Assert.Equal("login@saw.local", account.Email);
    }

    [Theory]
    [InlineData("SUPPLIER")][InlineData("UNKNOWN")]
    public async Task SupplierAndUnknownRoles_CannotUsePersonalProfileApi(string role)
    {
        account.Role.RoleCode = role;
        await Assert.ThrowsAsync<ForbiddenException>(() => Service.GetAsync(7, default));
        await Assert.ThrowsAsync<ForbiddenException>(() => Service.UpdateAsync(7, Request, default));
        AssertUnchanged();
    }

    [Fact]
    public async Task MissingAccountOrBusinessProfile_ReturnsNotFoundWithoutSaving()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Service.GetAsync(999, default));
        await Assert.ThrowsAsync<NotFoundException>(() => Service.UpdateAsync(999, Request, default));
        account.Role.RoleCode = "DISTRIBUTOR";
        account.Distributor = null;
        await Assert.ThrowsAsync<NotFoundException>(() => Service.UpdateAsync(7, Request with { Distributor = Business }, default));
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancellationAndPersistenceFailures_AreNotReportedAsSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        repository.Setup(x => x.GetByAccountIdAsync(7, cancellation.Token)).ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => Service.GetAsync(7, cancellation.Token));
        repository.Setup(x => x.SaveChangesAsync(CancellationToken.None)).ThrowsAsync(new InvalidOperationException("write failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.UpdateAsync(7, Request, default));
    }

    private async Task AssertInvalid(UpdateUserProfileRequest request)
    {
        var exception = await Assert.ThrowsAsync<ValidationException>(() => Service.UpdateAsync(7, request, default));
        Assert.NotEmpty(exception.Errors);
        AssertUnchanged();
    }
    private void AssertUnchanged()
    {
        Assert.Equal("Original name", account.FullName);
        Assert.Equal("Original company", account.Distributor!.DistributorName);
        Assert.Null(account.UpdatedAt);
        Assert.Null(account.Distributor.UpdatedAt);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
