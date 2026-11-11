using Moq;
using SAW.Application.Features.Suppliers.Commands;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Application.Repositories.Suppliers;
using SAW.Domain.Entities;

namespace SAW.Test.Application.Suppliers;

public sealed class SupplierProfileServiceTests
{
    private readonly Mock<ISupplierRepository> repository = new();
    private SupplierCommandService Service => new(repository.Object);
    private static DeclareSupplierProfileRequest Request => new()
    {
        SupplierName = " Hợp tác xã ", TaxCode = "0123456789", ContactPerson = " Liên hệ ", LegalRepresentative = " Đại diện ",
        Address = " Đồng Tháp ", PhoneNumber = " 0901234567 ", Email = " contact@saw.local ", SupplierType = "Hợp tác xã", CropTypeIds = [2]
    };

    [Fact]
    public async Task ReadProfile_UsesCurrentAccountAndReturnsExistingResponse()
    {
        var profile = new SupplierProfileResponse { AccountId = 7, SupplierId = 9 };
        repository.Setup(x => x.GetProfileByAccountIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        Assert.Same(profile, await Service.GetMyProfileAsync(7));
    }

    [Fact]
    public async Task MissingOrForeignProfile_CannotBeRead()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service.GetMyProfileAsync(7));
        repository.Setup(x => x.GetProfileByAccountIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new SupplierProfileResponse { AccountId = 99 });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service.GetMyProfileAsync(7));
    }

    [Fact]
    public async Task ExistingProfileOrDuplicateTax_PreventsDeclaration()
    {
        repository.Setup(x => x.HasProfileAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.DeclareProfileAsync(7, Request));
        repository.Setup(x => x.HasProfileAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        repository.Setup(x => x.IsTaxCodeExistsAsync(Request.TaxCode, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await Assert.ThrowsAsync<ArgumentException>(() => Service.DeclareProfileAsync(7, Request));
        repository.Verify(x => x.AddSupplierAsync(It.IsAny<Supplier>(), It.IsAny<List<int>>(), It.IsAny<List<SupplierCertificationInputDto>>(),
            It.IsAny<List<SupplierGrowingAreaInputDto>>(), It.IsAny<string?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Declaration_PreservesSupplierSpecificFieldsAndNormalizesContactData()
    {
        var profile = new SupplierProfileResponse { AccountId = 7, SupplierId = 9 };
        repository.Setup(x => x.GenerateSupplierCodeAsync(It.IsAny<CancellationToken>())).ReturnsAsync("SUP-000009");
        repository.Setup(x => x.GetProfileByAccountIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        Assert.Same(profile, await Service.DeclareProfileAsync(7, Request));
        repository.Verify(x => x.AddSupplierAsync(It.Is<Supplier>(s => s.AccountId == 7 && s.SupplierCode == "SUP-000009"
                && s.SupplierName == "Hợp tác xã" && s.PhoneNumber == "0901234567" && s.Email == "contact@saw.local"
                && s.Address == "Đồng Tháp" && s.ContactPerson == "Liên hệ | Đại diện PL: Đại diện"
                && s.Note == "Hợp tác xã" && s.ProfileStatus == "ACTIVE"),
            It.Is<List<int>>(ids => ids.SequenceEqual(new[] { 2 })), It.IsAny<List<SupplierCertificationInputDto>>(),
            It.IsAny<List<SupplierGrowingAreaInputDto>>(), It.IsAny<string?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MissingSupplier_CannotBeUpdated()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service.UpdateProfileAsync(7, new UpdateSupplierProfileRequest()));
    }

    private Supplier ExistingSupplier()
    {
        var supplier = new Supplier { SupplierId = 9, AccountId = 7, TaxCode = "0123456789", SupplierName = "Original", PhoneNumber = "0901111111", Email = "old@example.com", ProfileStatus = "ACTIVE" };
        repository.Setup(r => r.GetEntityByAccountIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(supplier);
        repository.Setup(r => r.GetProfileByAccountIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(() => new SupplierProfileResponse { AccountId = 7, SupplierId = 9, TaxCode = supplier.TaxCode, PhoneNumber = supplier.PhoneNumber, Email = supplier.Email });
        return supplier;
    }
    private static UpdateSupplierProfileRequest UpdateRequest(string? phone = "0901234567", string? email = "contact@example.com") => new()
    { SupplierName = "Updated", TaxCode = "0123456789", ContactPerson = "Contact", LegalRepresentative = "Owner", Address = "Address", PhoneNumber = phone, Email = email, CropTypeIds = [2] };

    [Theory]
    [InlineData("1111111111")][InlineData("")][InlineData(" ")][InlineData("GOOGLE-injected")]
    public async Task RegisteredTaxCodeCannotBeReplacedOrClearedBeforeAnyMutation(string taxCode)
    {
        var supplier = ExistingSupplier(); var request = UpdateRequest(); request.TaxCode = taxCode;
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Service.UpdateProfileAsync(7, request));
        Assert.Contains("Mã số thuế", error.Message); Assert.Equal("0123456789", supplier.TaxCode); Assert.Equal("Original", supplier.SupplierName);
        repository.Verify(r => r.UpdateSupplierAsync(It.IsAny<Supplier>(), It.IsAny<List<int>>(), It.IsAny<List<SupplierCertificationInputDto>>(), It.IsAny<List<SupplierGrowingAreaInputDto>>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("0901234567", "0901234567")]
    [InlineData("+84 901 234 567", "+84901234567")]
    [InlineData("090.123.4567", "0901234567")]
    [InlineData("090-123-4567", "0901234567")]
    public async Task ContactEditKeepsTaxCodeAndNormalizesPhone(string phone, string expected)
    {
        var supplier = ExistingSupplier(); var request = UpdateRequest(phone, " contact@example.com "); request.TaxCode = " 0123456789 ";
        var result = await Service.UpdateProfileAsync(7, request);
        Assert.Equal("0123456789", supplier.TaxCode); Assert.Equal("Updated", supplier.SupplierName); Assert.Equal(expected, result.PhoneNumber); Assert.Equal("contact@example.com", result.Email);
    }

    [Theory]
    [InlineData("1234567890", "valid@example.com")]
    [InlineData("090123456", "valid@example.com")]
    [InlineData("09012345678", "valid@example.com")]
    [InlineData("+840901234567", "valid@example.com")]
    [InlineData("0.....", "valid@example.com")]
    [InlineData("0901234567", "user@host")]
    [InlineData("0901234567", "Name <user@example.com>")]
    [InlineData("0901234567", "a..b@example.com")]
    [InlineData("0901234567", "a b@example.com")]
    public async Task InvalidContactsCannotBeDeclaredOrUsedToPartiallyUpdateSupplier(string phone, string email)
    {
        var supplier = ExistingSupplier(); var request = UpdateRequest(phone, email);
        await Assert.ThrowsAsync<ArgumentException>(() => Service.UpdateProfileAsync(7, request));
        await Assert.ThrowsAsync<ArgumentException>(() => Service.DeclareProfileAsync(7, request));
        Assert.Equal("Original", supplier.SupplierName); Assert.Equal("0901111111", supplier.PhoneNumber); Assert.Equal("old@example.com", supplier.Email);
        repository.Verify(r => r.AddSupplierAsync(It.IsAny<Supplier>(), It.IsAny<List<int>>(), It.IsAny<List<SupplierCertificationInputDto>>(), It.IsAny<List<SupplierGrowingAreaInputDto>>(), It.IsAny<string?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(r => r.UpdateSupplierAsync(It.IsAny<Supplier>(), It.IsAny<List<int>>(), It.IsAny<List<SupplierCertificationInputDto>>(), It.IsAny<List<SupplierGrowingAreaInputDto>>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
