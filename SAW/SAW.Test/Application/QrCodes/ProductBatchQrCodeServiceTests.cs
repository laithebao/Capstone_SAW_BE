using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SAW.Application.Features.QrCodes;
using SAW.Application.Features.QrCodes.Interfaces;
using SAW.Application.Features.QrCodes.Services;
using SAW.Application.Repositories;
using SAW.Domain.Entities;

namespace SAW.Test.Application.QrCodes;

public sealed class ProductBatchQrCodeServiceTests
{
    private readonly Mock<IQrCodeRepository> _repository = new();
    private readonly Mock<IQrImageStorage> _storage = new();
    private readonly Mock<IQrCodeRenderer> _renderer = new();
    private readonly QrCodeOptions _options = new() { PublicFrontendBaseUrl = "https://saw.example" };
    private readonly BatchQrSnapshot _snapshot = new(
        new ProductBatch { ProductBatchId = 1, BatchCode = "B1", BatchStatus = "APPROVED_FOR_STORAGE", QualityGrade = "A" },
        new QcInspection { InspectionStatus = "COMPLETED", QcResult = "PASS", QualityGrade = "A",
            StartedAt = DateTime.UtcNow.AddMinutes(-5), CompletedAt = DateTime.UtcNow }, []);

    public ProductBatchQrCodeServiceTests()
    {
        _repository.Setup(r => r.GetAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(_snapshot);
        _storage.SetupGet(s => s.IsConfigured).Returns(true);
        _storage.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://res.cloudinary.com/test/image/upload/test.png");
        _renderer.Setup(r => r.RenderPng(It.IsAny<string>())).Returns([137, 80, 78, 71]);
    }

    private ProductBatchQrCodeService Service() => new(_repository.Object, _renderer.Object, _storage.Object,
        _options, NullLogger<ProductBatchQrCodeService>.Instance);

    [Theory]
    [InlineData("APPROVED_FOR_STORAGE", "COMPLETED", "PASS", "A", true)]
    [InlineData("RECEIVED", "COMPLETED", "PASS", "B", true)]
    [InlineData("IN_STOCK", "COMPLETED", "PASS", "C", true)]
    [InlineData("APPROVED_FOR_STORAGE", "COMPLETED", "PASS", "D", true)]
    [InlineData("QUARANTINE", "COMPLETED", "PASS", "D", false)]
    [InlineData("APPROVED_FOR_STORAGE", "COMPLETED", "PASS", "E", false)]
    [InlineData("APPROVED_FOR_STORAGE", "DRAFT", "PASS", "A", false)]
    [InlineData("APPROVED_FOR_STORAGE", "IN_PROGRESS", "PASS", "A", false)]
    [InlineData("APPROVED_FOR_STORAGE", "COMPLETED", "FAIL", "A", false)]
    [InlineData("SUBMITTED", "COMPLETED", "PASS", "A", false)]
    [InlineData("PENDING_QC", "COMPLETED", "PASS", "A", false)]
    [InlineData("REJECTED", "COMPLETED", "PASS", "A", false)]
    [InlineData("CANCELLED", "COMPLETED", "PASS", "A", false)]
    [InlineData("RESERVED", "COMPLETED", "PASS", "A", false)]
    public async Task EligibilityControlsUpload(string status, string inspectionStatus, string result, string grade, bool allowed)
    {
        _snapshot.Batch.BatchStatus = status;
        _snapshot.Batch.QualityGrade = grade;
        _snapshot.LatestInspection!.QualityGrade = grade;
        _snapshot.LatestInspection.InspectionStatus = inspectionStatus;
        _snapshot.LatestInspection.QcResult = result;
        await Service().GenerateAsync(1, default);
        _storage.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()),
            allowed ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task MissingOrInconsistentQc_DoesNotGenerate()
    {
        _repository.Setup(r => r.GetAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(_snapshot with { LatestInspection = null });
        await Service().GenerateAsync(1, default);
        _repository.Setup(r => r.GetAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(_snapshot);
        _snapshot.Batch.QualityGrade = "B";
        await Service().GenerateAsync(1, default);
        _snapshot.Batch.QualityGrade = "A";
        _snapshot.LatestInspection!.CompletedAt = null;
        await Service().GenerateAsync(1, default);
        _storage.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("RESERVED", "READY")]
    [InlineData("PARTIALLY_ISSUED", "READY")]
    [InlineData("ISSUED", "READY")]
    [InlineData("REJECTED", "UNAVAILABLE")]
    [InlineData("CANCELLED", "UNAVAILABLE")]
    [InlineData("QUARANTINE", "UNAVAILABLE")]
    public async Task ReadExistingQr_HasSeparateEligibility(string status, string expected)
    {
        _snapshot.Batch.BatchStatus = status;
        _repository.Setup(r => r.GetAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(_snapshot with
        {
            Codes = [new QrCode { IsActive = true, PublicToken = "stable", TraceabilityUrl = "https://original/trace/stable",
                QrImageUrl = "https://image/qr.png", GeneratedAt = new DateTime(2026, 1, 1) }]
        });
        var response = await Service().GetAsync(1, default);
        Assert.Equal(expected, response.Status);
        Assert.Equal(expected == "READY" ? "stable" : null, response.PublicToken);
        _storage.VerifyNoOtherCalls();
        _repository.Verify(r => r.SaveGeneratedAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingReadyOrInactiveCode_NeverUploads(bool active)
    {
        _repository.Setup(r => r.GetAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(_snapshot with
        { Codes = [new QrCode { IsActive = active, QrImageUrl = active ? "https://image/qr.png" : null }] });
        await Service().GenerateAsync(1, default);
        Assert.Equal(active ? "READY" : "UNAVAILABLE", (await Service().GetAsync(1, default)).Status);
        _storage.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MissingImage_RepairsUsingOriginalTokenAndUrl()
    {
        _repository.Setup(r => r.GetAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(_snapshot with
        { Codes = [new QrCode { IsActive = true, PublicToken = "original", TraceabilityUrl = "https://old.example/trace/original" }] });
        _repository.Setup(r => r.SaveGeneratedAsync(1, "original", "https://old.example/trace/original", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QrCode { QrImageUrl = "https://res.cloudinary.com/test/image/upload/test.png" });
        await Service().GenerateAsync(1, default);
        _renderer.Verify(r => r.RenderPng("https://old.example/trace/original"), Times.Once);
        _storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Generation_EncodesRandomPublicUrl_AndKeepsPersistedImage()
    {
        string? encoded = null;
        _renderer.Setup(r => r.RenderPng(It.IsAny<string>())).Callback<string>(url => encoded = url).Returns([1]);
        _repository.Setup(r => r.SaveGeneratedAsync(1, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long id, string token, string url, string image, CancellationToken _) =>
            {
                Assert.Matches("^[a-f0-9]{64}$", token);
                Assert.Equal("https://saw.example/trace/" + token, url);
                Assert.Equal(encoded, url);
                return new QrCode { QrImageUrl = image };
            });
        await Service().GenerateAsync(1, default);
        _storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UploadFailure_NoDatabaseWrite_CleansOnlyOwnAttempt()
    {
        string? uploaded = null;
        _storage.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], CancellationToken>((id, _, _) => uploaded = id).ThrowsAsync(new HttpRequestException());
        await Assert.ThrowsAsync<HttpRequestException>(() => Service().GenerateAsync(1, default));
        _repository.Verify(r => r.SaveGeneratedAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _storage.Verify(s => s.DeleteAsync(uploaded!, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DbFailure_ReconcilesCommitBeforeCleanup(bool committed)
    {
        _repository.Setup(r => r.SaveGeneratedAsync(1, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException());
        _repository.Setup(r => r.IsImageReferencedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(committed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().GenerateAsync(1, default));
        _storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), committed ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task LostRaceOrEligibility_CleansOwnAsset()
    {
        string? uploaded = null;
        _storage.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], CancellationToken>((id, _, _) => uploaded = id).ReturnsAsync("https://image/loser.png");
        _repository.Setup(r => r.SaveGeneratedAsync(1, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QrCode { QrImageUrl = "https://image/winner.png" });
        await Service().GenerateAsync(1, default);
        _storage.Verify(s => s.DeleteAsync(uploaded!, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MissingConfiguration_DoesNotUpload_AndReadStillWorks()
    {
        _options.PublicFrontendBaseUrl = "";
        await Service().GenerateAsync(1, default);
        Assert.Equal("PENDING", (await Service().GetAsync(1, default)).Status);
        _storage.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
