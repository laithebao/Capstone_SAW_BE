using Moq;
using SAW.Application.Features.Suppliers.Commands;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;
using SAW.Domain.Entities;

namespace SAW.Test.Application.Suppliers;

public class SupplierProfileCompletionTests
{
    private readonly Mock<ISupplierRepository> repository = new();
    private readonly Supplier supplier = new()
    {
        SupplierId = 2, AccountId = 1, SupplierCode = "SUP-000001", SupplierName = "Google user",
        TaxCode = "GOOGLE-original", ContactPerson = "Google user", Address = "", ProfileStatus = "PENDING_REVIEW",
        Account = new Account { AccountId = 1, Username = "google_verified", AccountStatus = "ACTIVE" }
    };

    public SupplierProfileCompletionTests()
    {
        repository.Setup(r => r.GetEntityByAccountIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(supplier);
        repository.Setup(r => r.GetProfileByAccountIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SupplierProfileResponse { AccountId = 1, SupplierId = 2, ProfileStatus = supplier.ProfileStatus });
    }

    private static UpdateSupplierProfileRequest Request(string? phone = "0901234567") => new()
    {
        SupplierName = "Test cooperative", TaxCode = "TEST-TAX", Address = "Test address",
        ContactPerson = "Test contact", LegalRepresentative = "Test representative", PhoneNumber = phone,
        Email = "supplier@test.invalid", CropTypeIds = [3]
    };
    private Task<SupplierProfileResponse> Complete(string? phone = "0901234567") =>
        new SupplierCommandService(repository.Object).UpdateProfileAsync(1, Request(phone));

    [Fact]
    public async Task VerifiedGoogleProfile_BecomesActiveWhenCompletionIsPersisted()
    {
        var response = await Complete();
        Assert.Equal("ACTIVE", response.ProfileStatus);
        repository.Verify(r => r.UpdateSupplierAsync(It.Is<Supplier>(s => s.ProfileStatus == "ACTIVE" && s.AccountId == 1),
            It.IsAny<List<int>>(), It.IsAny<List<SupplierCertificationInputDto>>(), It.IsAny<List<SupplierGrowingAreaInputDto>>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PreviouslyCompletedGoogleProfile_RecoversWhenSavedAgain()
    {
        supplier.TaxCode = "ALREADY-DECLARED";
        supplier.PhoneNumber = "0901234567";
        Assert.Equal("ACTIVE", (await Complete()).ProfileStatus);
    }

    [Theory]
    [InlineData("PENDING")][InlineData("LOCKED")][InlineData("INACTIVE")]
    public async Task UnverifiedOrBlockedAccount_IsNotActivated(string accountStatus)
    {
        supplier.Account.AccountStatus = accountStatus;
        Assert.Equal("PENDING_REVIEW", (await Complete()).ProfileStatus);
    }

    [Theory]
    [InlineData("INACTIVE")][InlineData("SUSPENDED")][InlineData("REJECTED")][InlineData("ACTIVE")]
    public async Task OtherProfileStatuses_AreNotChanged(string profileStatus)
    {
        supplier.ProfileStatus = profileStatus;
        Assert.Equal(profileStatus, (await Complete()).ProfileStatus);
    }

    [Fact]
    public async Task OrdinaryAccountPendingReview_IsNotActivatedByThisFix()
    {
        supplier.Account.Username = "regular_supplier";
        Assert.Equal("PENDING_REVIEW", (await Complete()).ProfileStatus);
    }

    [Fact]
    public async Task IncompleteGoogleProfile_RemainsPending()
    {
        Assert.Equal("PENDING_REVIEW", (await Complete(" ")).ProfileStatus);
    }

    [Fact]
    public async Task DuplicateTaxCode_DoesNotActivateOrPersistTheProfile()
    {
        repository.Setup(r => r.IsTaxCodeExistsExceptCurrentAsync("TEST-TAX", 2, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await Assert.ThrowsAsync<ArgumentException>(() => Complete());
        Assert.Equal("PENDING_REVIEW", supplier.ProfileStatus);
        repository.Verify(r => r.UpdateSupplierAsync(It.IsAny<Supplier>(), It.IsAny<List<int>>(),
            It.IsAny<List<SupplierCertificationInputDto>>(), It.IsAny<List<SupplierGrowingAreaInputDto>>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
