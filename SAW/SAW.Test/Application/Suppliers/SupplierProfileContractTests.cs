using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using SAW.Application.Features.Suppliers.DTOs;

namespace SAW.Test.Application.Suppliers;

public sealed class SupplierProfileContractTests
{
    private static DeclareSupplierProfileRequest Valid(bool update = false)
    {
        DeclareSupplierProfileRequest request = update ? new UpdateSupplierProfileRequest() : new();
        request.SupplierName = "Supplier"; request.TaxCode = "0123456789"; request.LegalRepresentative = "Representative";
        request.ContactPerson = "Contact"; request.Address = "Address"; request.PhoneNumber = "0901234567";
        request.Email = "contact@test.invalid"; request.CropTypeIds = [1];
        return request;
    }

    [Theory]
    [InlineData("SupplierName", "")]
    [InlineData("TaxCode", "")]
    [InlineData("LegalRepresentative", " ")]
    [InlineData("ContactPerson", "")]
    [InlineData("Address", "")]
    [InlineData("Email", "invalid-email")]
    [InlineData("PhoneNumber", "letters")]
    public void CreateAndUpdate_EnforceRequiredFieldsAndContactFormats(string field, string value)
    {
        foreach (var update in new[] { false, true })
        {
            var request = Valid(update); request.GetType().GetProperty(field)!.SetValue(request, value);
            var errors = new List<ValidationResult>();
            Assert.False(Validator.TryValidateObject(request, new ValidationContext(request), errors, true));
            Assert.Contains(errors, error => error.MemberNames.Contains(field));
        }
    }

    [Theory]
    [InlineData("SupplierName", 200)]
    [InlineData("TaxCode", 50)]
    [InlineData("SupplierType", 100)]
    [InlineData("LegalRepresentative", 100)]
    [InlineData("ContactPerson", 100)]
    [InlineData("Address", 500)]
    public void CreateAndUpdate_EnforceCurrentFieldLengthBoundaries(string field, int max)
    {
        foreach (var update in new[] { false, true })
        {
            var request = Valid(update); var property = request.GetType().GetProperty(field)!;
            property.SetValue(request, new string('x', max));
            Assert.True(Validator.TryValidateObject(request, new ValidationContext(request), [], true));
            property.SetValue(request, new string('x', max + 1));
            Assert.False(Validator.TryValidateObject(request, new ValidationContext(request), [], true));
        }
    }

    [Fact]
    public void BothProfileRequestsRequireAtLeastOneCropType()
    {
        foreach (var update in new[] { false, true })
        {
            var request = Valid(update); request.CropTypeIds = [];
            var errors = new List<ValidationResult>();
            Assert.False(Validator.TryValidateObject(request, new ValidationContext(request), errors, true));
            Assert.Contains(errors, error => error.MemberNames.Contains(nameof(request.CropTypeIds)));
        }
    }

    [Fact]
    public void CertificationNormalization_AcceptsLegacyNamesAndTypedEvidenceWithoutConvertingManagedDocumentsIntoCertificates()
    {
        var request = Valid();
        request.Certifications = JsonSerializer.SerializeToElement(new object[]
        {
            "VietGAP", new { certificationName = "GlobalGAP", evidenceFileUrl = "https://legacy.test/global.pdf" }, " ", new { certificationName = "" }
        });
        request.EvidenceDocumentUrls = ["/api/supplier-files/00000000-0000-0000-0000-000000000001"];
        var certifications = request.GetNormalizedCertifications();
        Assert.Equal(new[] { "VietGAP", "GlobalGAP" }, certifications.Select(c => c.CertificationName));
        Assert.Equal("https://legacy.test/global.pdf", certifications[1].EvidenceFileUrl);
        Assert.Null(certifications[0].EvidenceFileUrl);
    }

    [Fact]
    public void LegacyEvidenceUrlsStillAttachToLegacyCertificationRecords()
    {
        var request = Valid(); request.Certifications = JsonSerializer.SerializeToElement(new[] { "VietGAP" });
        request.EvidenceDocumentUrls = ["https://legacy.test/first.pdf", "https://legacy.test/extra.pdf"];
        var certifications = request.GetNormalizedCertifications();
        Assert.Equal("VietGAP", certifications[0].CertificationName);
        Assert.Equal("https://legacy.test/first.pdf", certifications[0].EvidenceFileUrl);
        Assert.Equal("https://legacy.test/extra.pdf", certifications[1].EvidenceFileUrl);
    }
}
