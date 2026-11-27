using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.CropTypes;
using SAW.Domain.Entities;

namespace SAW.Test.Application.CropTypes;

public sealed class CropTypeServiceTests
{
    private readonly Mock<ICropTypeRepository> repository = new();
    private readonly CropType crop = new() { CropTypeId = 4, CropCode = "OLD", CropName = "Old", CategoryName = "Fruit", DefaultUnit = "Kg", IsActive = true };
    private CropTypeService Service => new(repository.Object);
    private static SaveCropTypeRequest Request => new(" cam ", " Cam ", " Fruit ", 2, 2, 8, 60, 90, 30, 100, " Kg ");

    public CropTypeServiceTests()
    {
        repository.Setup(x => x.GetByIdAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync(crop);
        repository.Setup(x => x.GetCategoryAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(new Category { CategoryId = 2, ParentId = 1, Name = "Citrus", IsActive = true, Parent = new Category { CategoryId = 1, Name = "Fruit", IsActive = true } });
    }

    [Fact]
    public async Task Search_NormalizesFiltersAndPagination_AndMapsResults()
    {
        repository.Setup(x => x.SearchAsync("Cam", "Fruit", true, 1, 100, It.IsAny<CancellationToken>())).ReturnsAsync((new[] { crop }, 9));
        var result = await Service.SearchAsync(" Cam ", " Fruit ", true, -1, 999, default);
        Assert.Equal(9, result.TotalCount);
        Assert.Equal(1, result.Page);
        Assert.Equal(100, result.PageSize);
        Assert.Equal("OLD", Assert.Single(result.Items).Code);
    }

    [Fact]
    public async Task Create_TrimsFieldsAndUppercasesCode_BeforeSaving()
    {
        var result = await Service.CreateAsync(Request, default);
        Assert.Equal("CAM", result.Code);
        Assert.Equal("Cam", result.Name);
        Assert.Equal("Fruit", result.CategoryName);
        Assert.Equal(2, result.CategoryId);
        Assert.Equal(2, result.MinTemperature);
        Assert.Equal(90, result.MaxHumidity);
        Assert.Equal(30, result.ShelfLifeDays);
        Assert.Equal(100, result.SafetyStockLevelKg);
        Assert.Equal("Kg", result.DefaultUnit);
        repository.Verify(x => x.Add(It.Is<CropType>(c => c.CropCode == "CAM" && c.CreatedAt != default)), Times.Once);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BasicEditPreservesExistingStorageAndScientificDescription()
    {
        crop.ExpectedMinTempC = 3; crop.ExpectedMaxTempC = 8;
        crop.ExpectedMinHumidityPct = 70; crop.ExpectedMaxHumidityPct = 90;
        crop.ShelfLifeDays = 60; crop.SafetyStockLevelKg = 150; crop.DefaultUnit = "thùng"; crop.Description = "Legacy";
        var result = await Service.UpdateBasicAsync(4, new("NEW", " ngô ST25 ", 2), default);
        Assert.Equal("Ngô ST25", result.Name); Assert.Equal("thùng", result.DefaultUnit);
        Assert.Equal(3, result.MinTemperature); Assert.Equal(8, result.MaxTemperature);
        Assert.Equal(70, result.MinHumidity); Assert.Equal(90, result.MaxHumidity);
        Assert.Equal(60, result.ShelfLifeDays); Assert.Equal(150, result.SafetyStockLevelKg); Assert.Equal("Legacy", crop.Description);
    }

    [Fact]
    public async Task BasicCreateUsesNeutralStorageDefaultsAndCapitalizesOnlyFirstLetter()
    {
        var result = await Service.CreateBasicAsync(new("MA_NGO", " ngô ST25 ", 2), default);
        Assert.Equal("Ngô ST25", result.Name); Assert.Equal("kg", result.DefaultUnit);
        Assert.Null(result.MinTemperature); Assert.Null(result.MaxTemperature); Assert.Null(result.ShelfLifeDays);
    }

    [Fact]
    public async Task Update_ExcludesCurrentIdFromUniquenessChecks_AndPreservesDescription()
    {
        crop.Description = "Legacy scientific description";
        var result = await Service.UpdateAsync(4, Request, default);
        Assert.Equal(4, result.Id);
        Assert.Equal("Legacy scientific description", crop.Description);
        Assert.NotNull(result.UpdatedAt);
        repository.Verify(x => x.CodeExistsAsync("cam", 4, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(x => x.NameExistsAsync("Cam", 4, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task ReversedTemperatureOrHumidity_RejectsBeforeSaving(bool temperature)
    {
        var request = temperature ? Request with { MinTemperature = 20 } : Request with { MinHumidity = 95 };
        await Assert.ThrowsAsync<BadRequestException>(() => Service.CreateAsync(request, default));
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.Add(It.IsAny<CropType>()), Times.Never);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task DuplicateCodeOrName_RejectsWithoutChangingExistingCrop(bool code)
    {
        if (code) repository.Setup(x => x.CodeExistsAsync("cam", 4, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        else repository.Setup(x => x.NameExistsAsync("Cam", 4, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await Assert.ThrowsAsync<ConflictException>(() => Service.UpdateAsync(4, Request, default));
        Assert.Equal("OLD", crop.CropCode);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetStatus_ChangesOnlyActivationAndTimestamp()
    {
        var result = await Service.SetStatusAsync(4, false, default);
        Assert.False(result.IsActive);
        Assert.Equal("OLD", result.Code);
        Assert.NotNull(result.UpdatedAt);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InUseCrop_CannotBeDeleted()
    {
        repository.Setup(x => x.IsInUseAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await Assert.ThrowsAsync<ConflictException>(() => Service.DeleteAsync(4, default));
        repository.Verify(x => x.Remove(It.IsAny<CropType>()), Times.Never);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnusedCrop_IsDeletedAndSaved()
    {
        await Service.DeleteAsync(4, default);
        repository.Verify(x => x.Remove(crop), Times.Once);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MissingCrop_ReturnsNotFoundForReadAndEveryMutation()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Service.GetAsync(99, default));
        await Assert.ThrowsAsync<NotFoundException>(() => Service.UpdateAsync(99, Request, default));
        await Assert.ThrowsAsync<NotFoundException>(() => Service.SetStatusAsync(99, false, default));
        await Assert.ThrowsAsync<NotFoundException>(() => Service.DeleteAsync(99, default));
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("25.678", "25.68")]
    [InlineData("1.005", "1.01")]
    [InlineData("-1.005", "-1.01")]
    [InlineData("2.5", "2.5")]
    public async Task CreateAndUpdateRoundStorageConditionsToTwoDecimalsWithoutChangingWeightOrShelfLife(string input, string expected)
    {
        var value = decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture);
        var rounded = decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);
        var request = Request with { MinTemperature = value, MaxTemperature = 30.126m, MinHumidity = 55.555m, MaxHumidity = 89.995m, SafetyStockLevelKg = 100.123m };
        foreach (var result in new[] { await Service.CreateAsync(request, default), await Service.UpdateAsync(4, request, default) })
        {
            Assert.Equal(rounded, result.MinTemperature); Assert.Equal(30.13m, result.MaxTemperature);
            Assert.Equal(55.56m, result.MinHumidity); Assert.Equal(90m, result.MaxHumidity);
            Assert.Equal(100.123m, result.SafetyStockLevelKg); Assert.Equal(30, result.ShelfLifeDays);
        }
    }
}
