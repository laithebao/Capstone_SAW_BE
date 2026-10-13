using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.GoodsReceipts.Dtos;
using SAW.Application.Features.GoodsReceipts.Services;
using SAW.Application.Repositories;

namespace SAW.Test.Application.GoodsReceipts;

public sealed class GoodsReceiptContractTests
{
    private readonly Mock<IGoodsReceiptQueryRepository> _read = new();
    private readonly Mock<IGoodsReceiptRepository> _write = new();
    private GoodsReceiptService Service() => new(_read.Object, _write.Object);
    private static readonly GoodsReceiptSnapshot Version = new(2, new DateTime(2026, 1, 2), "Before");
    private static GoodsReceiptDetail Detail() => new(7, "GR-7", 3, "B-3", "Rice", 4, "Supplier", 10, "Box", 100,
        2, "Zone", new DateOnly(2026, 1, 2), "DRAFT", null, "Before", "Staff", Version);

    [Fact]
    public async Task Search_normalizes_and_passes_server_filters_sort_and_page()
    {
        var query = new GoodsReceiptQuery("  GR-7  ", 4, 2, new(2026, 1, 1), new(2026, 1, 3), "DRAFT", "idAsc", 2, 10);
        var response = new GoodsReceiptPage<GoodsReceiptDetail>([Detail()], 11, 2, 10);
        _read.Setup(r => r.SearchAsync(It.IsAny<GoodsReceiptQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(response);
        Assert.Same(response, await Service().SearchAsync(query, default));
        var normalized = query with { Search = "GR-7" };
        _read.Verify(r => r.SearchAsync(normalized, It.IsAny<CancellationToken>()), Times.Once);
        _write.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("paging")]
    [InlineData("overflow")]
    [InlineData("longSearch")]
    [InlineData("controlSearch")]
    [InlineData("date")]
    [InlineData("status")]
    [InlineData("sort")]
    [InlineData("supplier")]
    [InlineData("location")]
    public async Task Invalid_list_filter_does_not_query(string input)
    {
        var query = input switch
        {
            "paging" => new GoodsReceiptQuery(Page: 0),
            "overflow" => new GoodsReceiptQuery(Page: int.MaxValue, PageSize: 100),
            "longSearch" => new GoodsReceiptQuery(Search: new string('x', 101)),
            "controlSearch" => new GoodsReceiptQuery(Search: "GR\n"),
            "date" => new GoodsReceiptQuery(FromDate: new(2026, 1, 3), ToDate: new(2026, 1, 1)),
            "status" => new GoodsReceiptQuery(Status: "CANCELLED"),
            "sort" => new GoodsReceiptQuery(SortBy: "nameAsc"),
            "supplier" => new GoodsReceiptQuery(SupplierId: 0),
            _ => new GoodsReceiptQuery(WarehouseLocationId: -1)
        };
        await Assert.ThrowsAsync<BadRequestException>(() => Service().SearchAsync(query, default));
        _read.VerifyNoOtherCalls(); _write.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task List_page_size_boundaries_and_default_sort_are_valid(int size)
    {
        var empty = new GoodsReceiptPage<GoodsReceiptDetail>([], 0, 1, size);
        _read.Setup(r => r.SearchAsync(It.IsAny<GoodsReceiptQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(empty);
        Assert.Same(empty, await Service().SearchAsync(new(PageSize: size, SortBy: null), default));
        _read.Verify(r => r.SearchAsync(It.Is<GoodsReceiptQuery>(q => q.PageSize == size && q.SortBy == "receivedAtDesc"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Get_reopens_stored_draft_and_missing_receipt_is_not_found()
    {
        _read.Setup(r => r.GetAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(Detail());
        Assert.Equal(Version, (await Service().GetAsync(7, default)).Snapshot);
        await Assert.ThrowsAsync<NotFoundException>(() => Service().GetAsync(999, default));
        await Assert.ThrowsAsync<BadRequestException>(() => Service().GetAsync(0, default));
        _write.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Create_trims_note_and_reloads_the_created_draft()
    {
        _write.Setup(r => r.CreateAsync(It.IsAny<CreateGoodsReceiptRequest>(), 11, It.IsAny<CancellationToken>())).ReturnsAsync(7L);
        _read.Setup(r => r.GetAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(Detail());
        var result = await Service().CreateAsync(new(3, 2, new(2026, 1, 2), "  Received  "), 11, default);
        Assert.Equal(7, result.Id);
        _write.Verify(r => r.CreateAsync(new(3, 2, new(2026, 1, 2), "Received"), 11, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_draft_clears_note_preserves_snapshot_and_reloads()
    {
        _read.Setup(r => r.GetAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(Detail());
        await Service().UpdateDraftAsync(7, new(2, new(2026, 1, 2), "  ", Version), 11, default);
        _write.Verify(r => r.UpdateDraftAsync(7, new(2, new(2026, 1, 2), null, Version), 11,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_passes_exact_reviewed_snapshot_and_reloads()
    {
        _read.Setup(r => r.GetAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(Detail());
        await Service().ConfirmAsync(7, new(Version), 11, default);
        _write.Verify(r => r.ConfirmAsync(7, Version, 11, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Edit_and_confirm_require_actor_and_snapshot_before_any_write()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().UpdateDraftAsync(7, new(2, new(2026, 1, 2), null, Version), 0, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().ConfirmAsync(7, new(Version), 0, default));
        await Assert.ThrowsAsync<BadRequestException>(() => Service().UpdateDraftAsync(7, new(2, new(2026, 1, 2), null, null!), 11, default));
        await Assert.ThrowsAsync<BadRequestException>(() => Service().ConfirmAsync(7, new(Version with { WarehouseLocationId = 0 }), 11, default));
        _write.VerifyNoOtherCalls();
    }
}
