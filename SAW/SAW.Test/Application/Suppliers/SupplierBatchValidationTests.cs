using SAW.Application.Features.Suppliers.Commands;
using SAW.Application.Features.Suppliers.DTOs;

namespace SAW.Test.Application.Suppliers;

public class SupplierBatchValidationTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);
    private static DeclareProductBatchRequest Valid() => new()
    {
        CropTypeId = 1, GrowingAreaId = 2, ProductName = "Cam",
        HarvestDate = new(2026, 1, 1), DeclaredQuantity = 250m, Unit = "Kg"
    };

    [Theory]
    [InlineData("Thùng", 10, 10, 100)]
    [InlineData("Bao", 20, 25, 500)]
    public void PackagesCalculateActualWeight(string unit, int count, int perPackage, int expected)
    {
        var r = Valid(); r.Unit = unit; r.DeclaredQuantity = count; r.PackageCount = count; r.PackageUnitWeightKg = perPackage;
        Assert.Equal(expected, SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
    }

    [Fact]
    public void KgAndTonsCalculateWeight()
    {
        var r = Valid(); Assert.Equal(250m, SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
        r.Unit = "Tấn"; r.DeclaredQuantity = 0.25m;
        Assert.Equal(250m, SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
    }

    [Fact]
    public void OptionalFieldsAcceptNullAndZeroTemperature()
    {
        var r = Valid(); r.ExpectedMinTempC = 0;
        Assert.Equal(250m, SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
    }

    [Fact]
    public void PackageWeightCannotFallbackToCount()
    {
        var r = Valid(); r.Unit = "Bao"; r.DeclaredQuantity = 10; r.PackageCount = 10;
        Assert.Throws<ArgumentException>(() => SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
        r.PackageUnitWeightKg = 25; r.PackageCount = 9;
        Assert.Throws<ArgumentException>(() => SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.0001")]
    [InlineData("1000000000000000")]
    public void QuantityRejectsOutOfBounds(string value)
    {
        var r = Valid(); r.DeclaredQuantity = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentException>(() => SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
    }

    [Fact]
    public void ConvertedWeightAlsoMustFitDatabase()
    {
        var r = Valid(); r.Unit = "Tấn"; r.DeclaredQuantity = 1000000000000m;
        Assert.Throws<ArgumentException>(() => SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
    }

    [Fact]
    public void TextAndDecimalLimitsAreEnforced()
    {
        var r = Valid(); r.Note = new string('a', 1000); r.PackagingType = new string('a', 100);
        Assert.Equal(250m, SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
        r.Note += "a";
        Assert.Throws<ArgumentException>(() => SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
        r.Note = null; r.ExpectedMinTempC = 1.234m;
        Assert.Throws<ArgumentException>(() => SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
        r.ExpectedMinTempC = 10000m;
        Assert.Throws<ArgumentException>(() => SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
    }

    [Fact]
    public void HumidityAndMinMaxAreValidated()
    {
        var r = Valid(); r.ExpectedMinHumidityPct = 0; r.ExpectedMaxHumidityPct = 100;
        Assert.Equal(250m, SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
        r.ExpectedMaxHumidityPct = 101;
        Assert.Throws<ArgumentException>(() => SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
        r.ExpectedMaxHumidityPct = null; r.ExpectedMinTempC = 10; r.ExpectedMaxTempC = 5;
        Assert.Throws<ArgumentException>(() => SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
    }

    [Fact]
    public void DatesCannotViolateHarvestDate()
    {
        var r = Valid(); r.ExpiryDate = r.HarvestDate.AddDays(-1);
        Assert.Throws<ArgumentException>(() => SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
        r.ExpiryDate = null; r.ExpectedDeliveryDate = r.HarvestDate.AddDays(-1);
        Assert.Throws<ArgumentException>(() => SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
        r.ExpectedDeliveryDate = null; r.HarvestDate = Today.AddDays(1);
        Assert.Throws<ArgumentException>(() => SupplierBatchValidation.ValidateAndCalculateWeight(r, Today));
    }
}
