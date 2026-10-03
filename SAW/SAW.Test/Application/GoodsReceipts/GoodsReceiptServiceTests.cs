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
    [InlineData("COMPLETED", "PASS", "E")]
    public void Rejects_qc_that_cannot_approve_storage(string status, string result, string grade)
    {
        var batch = new ProductBatch { BatchStatus = "APPROVED_FOR_STORAGE", QualityGrade = grade,
            Unit = "Bao", VerifiedQuantity = 100, VerifiedWeightInKg = 1000 };
        var qc = new QcInspection { InspectionStatus = status, QcResult = result, QualityGrade = grade, CompletedAt = DateTime.UtcNow };
        Assert.Throws<ConflictException>(() => GoodsReceiptRules.EnsureEligible(batch, qc));
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
