using FluentAssertions;
using SAW.Domain.Entities;
using SAW.Infrastructure.AiForecast;
using Xunit;

namespace SAW.Test.Infrastructure.AiForecast;

public sealed class ForecastPromptBuilderTests
{
    [Fact]
    public void BuildSystemPrompt_ShouldIncludeMandatoryRolesAndHeadingRequirement()
    {
        // Act
        var systemPrompt = ForecastPromptBuilder.BuildSystemPrompt();

        // Assert
        systemPrompt.Should().NotBeNullOrWhiteSpace();
        systemPrompt.Should().Contain("Senior Agri-Business Commercial & Post-Harvest Strategist");
        systemPrompt.Should().Contain("### ⚠️ Nguyên Nhân Dẫn Đến Độ Hao Hụt Cao & Giải Pháp Khắc Phục");
        systemPrompt.Should().Contain("YÊU CẦU ĐỊNH DẠNG JSON");
    }

    [Fact]
    public void BuildUserPrompt_WithFullData_ShouldIncludeAllKeySectionsAndMetrics()
    {
        // Arrange
        var cropType = new CropType
        {
            CropName = "Khoai tây Đà Lạt",
            CategoryName = "Củ quả",
            ExpectedMinTempC = 4m,
            ExpectedMaxTempC = 8m,
            ExpectedMinHumidityPct = 85m,
            ExpectedMaxHumidityPct = 90m,
            ShelfLifeDays = 30
        };

        var batch = new ProductBatch
        {
            BatchCode = "LH-20261028-01",
            ProductName = "Khoai tây hồng",
            CropType = cropType,
            Supplier = new Supplier { SupplierName = "Hợp tác xã Rau Đà Lạt" },
            GrowingArea = new GrowingArea { AreaName = "Lạc Dương", District = "Lạc Dương", Province = "Lâm Đồng" },
            WeightInKg = 500m,
            HarvestDate = new DateOnly(2026, 10, 20),
            ExpiryDate = new DateOnly(2026, 11, 20),
            QualityGrade = "A",
            BatchStatus = "IN_STOCK",
            PackagingType = "Thùng carton 20kg"
        };

        var qcInspection = new QcInspection
        {
            InspectionCode = "QC-20261021-01",
            QcResult = "PASSED",
            QualityGrade = "A",
            CompletedAt = new DateTime(2026, 10, 21, 9, 30, 0, DateTimeKind.Utc),
            SensoryResult = new SensoryResult
            {
                FreshnessScore = 9,
                ColorScore = 9,
                RipenessScore = 4,
                SizeScore = 8,
                DamagePercentage = 1.5m,
                Note = "Củ đều, không trầy xước"
            },
            LabResult = new LabResult
            {
                ChemicalResidueStatus = "PASSED",
                ResidueValue = 0.01m,
                ResidueUnit = "mg/kg",
                PathogenStatus = "NEGATIVE"
            }
        };

        var envStats = new EnvironmentStats(
            AvgTempC: 6.2m,
            MinTempC: 4.5m,
            MaxTempC: 8.8m,
            AvgHumidityPct: 87m,
            TempDeviationCount: 1,
            LogCount: 72,
            PeriodHours: 72
        );

        var location = new WarehouseLocation
        {
            LocationCode = "KHO-L1-A01",
            ZoneName = "Kho Mát L1",
            RackName = "Kệ A",
            BinName = "Ô 01",
            MinTempC = 4m,
            MaxTempC = 8m,
            MinHumidityPct = 80m,
            MaxHumidityPct = 90m
        };

        var snapshot = new ForecastInputSnapshot(
            Batch: batch,
            QcInspection: qcInspection,
            EnvStats: envStats,
            RecentEnvLogs: new List<EnvironmentLog>(),
            CurrentLocation: location,
            DaysFromHarvest: 8,
            DaysUntilExpiry: 23,
            ShelfLifeConsumedPct: 25.8m
        );

        // Act
        var userPrompt = ForecastPromptBuilder.BuildUserPrompt(snapshot);

        // Assert
        userPrompt.Should().Contain("LH-20261028-01");
        userPrompt.Should().Contain("Khoai tây hồng");
        userPrompt.Should().Contain("Hợp tác xã Rau Đà Lạt");
        userPrompt.Should().Contain("Điểm độ tươi: 9/10");
        userPrompt.Should().Contain("Tỷ lệ dập nát/khuyết tật ban đầu:");
        userPrompt.Should().Contain("KHO-L1-A01");
        userPrompt.Should().Contain("Số lần đo bị LỆCH KHỎI NGƯỠNG BẢO QUẢN CHO PHÉP: 1 lần");
    }

    [Fact]
    public void BuildUserPrompt_WithoutQcAndEnvStats_ShouldOutputFallbackWarnings()
    {
        // Arrange
        var batch = new ProductBatch
        {
            BatchCode = "LH-NO-DATA-01",
            ProductName = "Cà chua bi",
            WeightInKg = 200m,
            HarvestDate = new DateOnly(2026, 10, 25),
            BatchStatus = "RECEIVED"
        };

        var snapshot = new ForecastInputSnapshot(
            Batch: batch,
            QcInspection: null,
            EnvStats: null,
            RecentEnvLogs: new List<EnvironmentLog>(),
            CurrentLocation: null,
            DaysFromHarvest: 3,
            DaysUntilExpiry: null,
            ShelfLifeConsumedPct: null
        );

        // Act
        var userPrompt = ForecastPromptBuilder.BuildUserPrompt(snapshot);

        // Assert: Không được throw Exception và phải có cảnh báo fallback chuẩn
        userPrompt.Should().NotBeNullOrWhiteSpace();
        userPrompt.Should().Contain("CHƯA CÓ KẾT QUẢ KIỂM ĐỊNH QC HOÀN TẤT");
        userPrompt.Should().Contain("CHƯA CÓ NHẬT KÝ CẢM BIẾN MÔI TRƯỜNG CHO LÔ HÀNG NÀY");
        userPrompt.Should().Contain("Chưa chỉ định vị trí kho cụ thể");
    }
}
