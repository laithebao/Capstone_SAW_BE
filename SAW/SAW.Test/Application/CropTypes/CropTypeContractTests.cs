using System.ComponentModel.DataAnnotations;
using System.Reflection;
using SAW.Application.Features.CropTypes;

namespace SAW.Test.Application.CropTypes;

public sealed class CropTypeContractTests
{
    private static SaveCropTypeRequest Valid => new("MANGO", "Mango", "Fruit", null, 2, 8, 60, 90, 30, 0, "kg");
    private static IReadOnlyList<ValidationResult> Errors(SaveCropTypeRequest request)
    {
        var errors = new List<ValidationResult>();
        // MVC binds validation metadata from the primary-constructor parameters of records.
        foreach (var parameter in typeof(SaveCropTypeRequest).GetConstructors().Single().GetParameters())
            Validator.TryValidateValue(typeof(SaveCropTypeRequest).GetProperty(parameter.Name!)!.GetValue(request),
                new ValidationContext(request) { MemberName = parameter.Name }, errors, parameter.GetCustomAttributes<ValidationAttribute>());
        return errors;
    }

    [Theory]
    [InlineData("Code", 30)]
    [InlineData("Name", 150)]
    [InlineData("CategoryName", 100)]
    [InlineData("DefaultUnit", 20)]
    public void RequiredMasterDataFieldsRejectWhitespaceAndOverlength(string field, int max)
    {
        var request = Valid; var property = typeof(SaveCropTypeRequest).GetProperty(field)!;
        property.SetValue(request, " "); Assert.Contains(Errors(request), error => error.MemberNames.Contains(field));
        property.SetValue(request, new string('a', max)); Assert.Empty(Errors(request));
        property.SetValue(request, new string('a', max + 1)); Assert.Contains(Errors(request), error => error.MemberNames.Contains(field));
    }

    [Theory]
    [InlineData("MinHumidity", -0.001, false)]
    [InlineData("MinHumidity", 0, true)]
    [InlineData("MaxHumidity", 100, true)]
    [InlineData("MaxHumidity", 100.001, false)]
    [InlineData("SafetyStockLevelKg", -0.001, false)]
    [InlineData("SafetyStockLevelKg", 0, true)]
    [InlineData("MinTemperature", -50, true)]
    [InlineData("MinTemperature", -50.001, false)]
    [InlineData("MaxTemperature", 100, true)]
    [InlineData("MaxTemperature", 100.001, false)]
    public void StorageConditionBoundsIncludeLegitimateColdStorageAndHumidityEndpoints(string field, double value, bool valid)
    {
        var request = Valid; typeof(SaveCropTypeRequest).GetProperty(field)!.SetValue(request, (decimal)value);
        Assert.Equal(valid, !Errors(request).Any(error => error.MemberNames.Contains(field)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3650, true)]
    [InlineData(3651, false)]
    public void ShelfLifeMustBePositiveAndWithinSupportedStorageHorizon(int days, bool valid)
    { Assert.Equal(valid, !Errors(Valid with { ShelfLifeDays = days }).Any(error => error.MemberNames.Contains("ShelfLifeDays"))); }

    [Fact]
    public void OptionalStorageSettingsRemainOptionalAndDescriptionHasItsOwnLimit()
    {
        Assert.Empty(Errors(Valid with { MinTemperature = null, MaxTemperature = null, MinHumidity = null, MaxHumidity = null, ShelfLifeDays = null, SafetyStockLevelKg = null }));
        Assert.Empty(Errors(Valid with { ScientificName = new string('a', 1000) }));
        Assert.Contains(Errors(Valid with { ScientificName = new string('a', 1001) }), error => error.MemberNames.Contains("ScientificName"));
    }
}
