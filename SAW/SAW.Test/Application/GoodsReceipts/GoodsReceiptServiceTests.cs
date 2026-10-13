using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsReceipts;
using SAW.Application.Features.GoodsReceipts.Dtos;
using SAW.Application.Features.GoodsReceipts.Services;
using SAW.Application.Repositories;
using SAW.Domain.Entities;

namespace SAW.Test.Application.GoodsReceipts;

public sealed class GoodsReceiptServiceTests
{
    [Theory]
    [InlineData("DRAFT", "PASS", "A")]
    [InlineData("IN_PROGRESS", "PASS", "A")]
    [InlineData("COMPLETED", "FAIL", "A")]
    [InlineData("COMPLETED", "FAIL", "B")]
    [InlineData("COMPLETED", "FAIL", "C")]
    [InlineData("COMPLETED", "FAIL", "D")]
    [InlineData("COMPLETED", "FAIL", "E")]
    [InlineData("COMPLETED", "PASS", "F")]
    public void Rejects_qc_that_cannot_approve_storage(string status, string result, string grade)
    {
        var batch = new ProductBatch { BatchStatus = "APPROVED_FOR_STORAGE", QualityGrade = grade,
            Unit = "Bao", VerifiedQuantity = 100, VerifiedWeightInKg = 1000 };
        var qc = new QcInspection { InspectionStatus = status, QcResult = result, QualityGrade = grade, CompletedAt = DateTime.UtcNow };
        Assert.Throws<ConflictException>(() => GoodsReceiptRules.EnsureEligible(batch, qc));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    [InlineData("E")]
    public void Completed_pass_accepts_all_valid_matching_grades(string grade)
    {
        var batch = new ProductBatch { BatchStatus = "APPROVED_FOR_STORAGE", QualityGrade = grade,
            Unit = "Bao", VerifiedQuantity = 100, VerifiedWeightInKg = 1000 };
        var qc = new QcInspection { InspectionStatus = "COMPLETED", QcResult = "PASS",
            QualityGrade = grade, CompletedAt = DateTime.UtcNow };
        GoodsReceiptRules.EnsureEligible(batch, qc);
    }

    [Theory]
    [InlineData("SUBMITTED")]
    [InlineData("PENDING_QC")]
    [InlineData("REJECTED")]
    [InlineData("QUARANTINE")]
    [InlineData("CANCELLED")]
    [InlineData("IN_STOCK")]
    public void E_pass_does_not_bypass_batch_status(string status)
    {
        var batch = new ProductBatch { BatchStatus = status, QualityGrade = "E",
            Unit = "Bao", VerifiedQuantity = 100, VerifiedWeightInKg = 1000 };
        var qc = new QcInspection { InspectionStatus = "COMPLETED", QcResult = "PASS",
            QualityGrade = "E", CompletedAt = DateTime.UtcNow };
        Assert.Throws<ConflictException>(() => GoodsReceiptRules.EnsureEligible(batch, qc));
    }

    [Fact]
    public void E_pass_still_requires_completed_qc_matching_grade_verified_amounts_and_unit()
    {
        var batch = new ProductBatch { BatchStatus = "APPROVED_FOR_STORAGE", QualityGrade = "E",
            Unit = "Bao", VerifiedQuantity = 100, VerifiedWeightInKg = 1000 };
        var qc = new QcInspection { InspectionStatus = "COMPLETED", QcResult = "PASS", QualityGrade = "E" };
        Assert.Throws<ConflictException>(() => GoodsReceiptRules.EnsureEligible(batch, null));
        Assert.Throws<ConflictException>(() => GoodsReceiptRules.EnsureEligible(batch, qc));
        qc.CompletedAt = DateTime.UtcNow;
        batch.QualityGrade = "B";
        Assert.Throws<ConflictException>(() => GoodsReceiptRules.EnsureEligible(batch, qc));
        batch.QualityGrade = "E";
        foreach (var invalid in new decimal?[] { null, 0m, -1m, 1.0001m, 1000000000000000m })
        {
            batch.VerifiedQuantity = invalid;
            Assert.Throws<ConflictException>(() => GoodsReceiptRules.EnsureEligible(batch, qc));
            batch.VerifiedQuantity = 100;
            batch.VerifiedWeightInKg = invalid;
            Assert.Throws<ConflictException>(() => GoodsReceiptRules.EnsureEligible(batch, qc));
            batch.VerifiedWeightInKg = 1000;
        }
        foreach (var invalid in new[] { "", " ", "kg\n", new string('x', 21) })
        {
            batch.Unit = invalid;
            Assert.Throws<ConflictException>(() => GoodsReceiptRules.EnsureEligible(batch, qc));
        }
    }

    [Fact]
    public async Task Invalid_date_note_and_paging_do_not_reach_write_repository()
    {
        var read = new Mock<IGoodsReceiptQueryRepository>(); var write = new Mock<IGoodsReceiptRepository>();
        var service = new GoodsReceiptService(read.Object, write.Object);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        await Assert.ThrowsAsync<BadRequestException>(() => service.CreateAsync(new(1, 1, today.AddDays(1), null), 1, default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.CreateAsync(new(1, 1, today, new string('x', 1001)), 1, default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.CreateAsync(new(1, 0, today, null), 1, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(new(1, 1, today, null), 0, default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.SearchAsync(new(Page: int.MaxValue, PageSize: 100), default));
        await Assert.ThrowsAsync<BadRequestException>(() => service.SearchAsync(new(Status: "CANCELLED"), default));
        write.VerifyNoOtherCalls();
    }
}
