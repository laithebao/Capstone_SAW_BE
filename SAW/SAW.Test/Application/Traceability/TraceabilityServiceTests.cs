using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.Traceability.Dtos;
using SAW.Application.Features.Traceability.Services;
using SAW.Application.Repositories;

namespace SAW.Test.Application.Traceability;

public sealed class TraceabilityServiceTests
{
    private const string Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly Mock<ITraceabilityRepository> _repository = new();
    private static TraceabilityData Data(string status = "APPROVED_FOR_STORAGE", bool active = true,
        string qcStatus = "COMPLETED", string result = "PASS", string grade = "A", string batchGrade = "A") => new()
    {
        IsActive = active, QrImageUrl = "https://image.test/qr.png", BatchCode = "TRACE-1", ProductName = "Rice",
        BatchStatus = status, BatchQualityGrade = batchGrade,
        LatestQc = new(qcStatus, result, grade, new DateTime(2026, 1, 1), new DateTime(2026, 1, 2)),
        Origin = new("Farm", "South", "Province"), SupplierName = "Supplier", CropTypeName = "Rice",
        HarvestDate = new DateOnly(2025, 12, 25)
    };

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("../../../")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task InvalidToken_IsNotFoundWithoutQueryingDatabase(string token)
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(() => new TraceabilityService(_repository.Object).GetAsync(token, default));
        Assert.Equal(TraceabilityService.NotFoundMessage, error.Message);
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnknownValidToken_IsNotFound()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(() => new TraceabilityService(_repository.Object).GetAsync(Token, default));
        Assert.Equal(TraceabilityService.NotFoundMessage, error.Message);
    }

    [Theory]
    [InlineData("APPROVED_FOR_STORAGE")]
    [InlineData("RECEIVED")]
    [InlineData("IN_STOCK")]
    [InlineData("RESERVED")]
    [InlineData("PARTIALLY_ISSUED")]
    [InlineData("ISSUED")]
    public async Task AcceptedReadStatuses_WorkWithoutInventoryOrReceipt(string status)
    {
        _repository.Setup(r => r.GetByPublicTokenAsync(Token, It.IsAny<CancellationToken>())).ReturnsAsync(Data(status));
        var response = await new TraceabilityService(_repository.Object).GetAsync(Token, default);
        Assert.Equal("A", response.Quality.Grade);
        Assert.Equal("QC_COMPLETED", Assert.Single(response.Milestones).Type);
        Assert.Contains("chưa đủ", response.Quality.GradeExplanation);
    }

    [Theory]
    [InlineData("REJECTED", true, "COMPLETED", "PASS", "A", "A")]
    [InlineData("QUARANTINE", true, "COMPLETED", "PASS", "D", "D")]
    [InlineData("CANCELLED", true, "COMPLETED", "PASS", "A", "A")]
    [InlineData("SUBMITTED", true, "COMPLETED", "PASS", "A", "A")]
    [InlineData("PENDING_QC", true, "COMPLETED", "PASS", "A", "A")]
    [InlineData("IN_STOCK", false, "COMPLETED", "PASS", "A", "A")]
    [InlineData("IN_STOCK", true, "DRAFT", "PASS", "A", "A")]
    [InlineData("IN_STOCK", true, "IN_PROGRESS", "PASS", "A", "A")]
    [InlineData("IN_STOCK", true, "COMPLETED", "FAIL", "A", "A")]
    [InlineData("IN_STOCK", true, "COMPLETED", "PASS", "E", "E")]
    [InlineData("IN_STOCK", true, "COMPLETED", "PASS", "A", "B")]
    public async Task InactiveOrInconsistentData_IsGone(string status, bool active, string qcStatus, string result, string grade, string batchGrade)
    {
        _repository.Setup(r => r.GetByPublicTokenAsync(Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Data(status, active, qcStatus, result, grade, batchGrade));
        var error = await Assert.ThrowsAsync<GoneException>(() => new TraceabilityService(_repository.Object).GetAsync(Token, default));
        Assert.Equal(TraceabilityService.UnavailableMessage, error.Message);
    }

    [Fact]
    public async Task MissingQc_AndMissingReadyImage_AreGone()
    {
        _repository.Setup(r => r.GetByPublicTokenAsync(Token, It.IsAny<CancellationToken>())).ReturnsAsync(new TraceabilityData
        { IsActive = true, BatchStatus = "IN_STOCK", BatchQualityGrade = "A", QrImageUrl = "https://image.test/qr.png" });
        await Assert.ThrowsAsync<GoneException>(() => new TraceabilityService(_repository.Object).GetAsync(Token, default));
        _repository.Setup(r => r.GetByPublicTokenAsync(Token, It.IsAny<CancellationToken>())).ReturnsAsync(new TraceabilityData
        { IsActive = true, BatchStatus = "IN_STOCK", BatchQualityGrade = "A", LatestQc = Data().LatestQc });
        await Assert.ThrowsAsync<GoneException>(() => new TraceabilityService(_repository.Object).GetAsync(Token, default));
    }

    [Fact]
    public async Task FullyIssuedBatch_KeepsQualityAndCommittedMilestones()
    {
        _repository.Setup(r => r.GetByPublicTokenAsync(Token, It.IsAny<CancellationToken>())).ReturnsAsync(new TraceabilityData
        {
            IsActive = true, QrImageUrl = "https://image.test/qr.png", BatchStatus = "ISSUED", BatchQualityGrade = "A",
            LatestQc = Data().LatestQc, ReceiptMilestones = [new DateTime(2026, 1, 3)],
            IssueMilestones = [new DateTime(2026, 1, 4)]
        });
        var response = await new TraceabilityService(_repository.Object).GetAsync(Token, default);
        Assert.Equal("PASS", response.Quality.Result);
        Assert.Equal(new[] { "QC_COMPLETED", "RECEIVED", "ISSUED" }, response.Milestones.Select(m => m.Type));
    }
}
