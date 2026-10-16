using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.GoodsIssues;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/operation/goods-issues")]
[Authorize(Roles = "OPERATION_STAFF")]
public sealed class GoodsIssuesController(GoodsIssueService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] GoodsIssueQuery query, CancellationToken ct) =>
        Ok(ApiResponse<GoodsIssuePage<GoodsIssueSummary>>.Success(await service.SearchAsync(query, ct)));
    [HttpGet("locations")]
    public async Task<IActionResult> Locations(CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<GoodsIssueLocation>>.Success(await service.LocationsAsync(ct)));
    [HttpGet("eligible-orders")]
    public async Task<IActionResult> Eligible(string? search, CancellationToken ct, int page = 1, int pageSize = 20) =>
        Ok(ApiResponse<GoodsIssuePage<GoodsIssueOrder>>.Success(await service.EligibleAsync(search, page, pageSize, ct)));
    [HttpGet("eligible-orders/{id:long}")]
    public async Task<IActionResult> Order(long id, CancellationToken ct) => Ok(ApiResponse<GoodsIssueOrder>.Success(await service.OrderAsync(id, ct)));
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct) => Ok(ApiResponse<GoodsIssueDetail>.Success(await service.GetAsync(id, ct)));
    [HttpPost]
    public async Task<IActionResult> Create(CreateGoodsIssueRequest request, CancellationToken ct) =>
        Ok(ApiResponse<GoodsIssueDetail>.Success(await service.CreateAsync(request, ActorId(), ct), "Đã lưu phiếu xuất nháp; tồn kho chưa thay đổi."));
    [HttpPut("{id:long}/draft")]
    public async Task<IActionResult> Update(long id, UpdateGoodsIssueDraftRequest request, CancellationToken ct) =>
        Ok(ApiResponse<GoodsIssueDetail>.Success(await service.UpdateDraftAsync(id, request, ActorId(), ct), "Đã lưu thay đổi phiếu nháp."));
    [HttpPost("{id:long}/confirm")]
    public async Task<IActionResult> Confirm(long id, ConfirmGoodsIssueRequest request, CancellationToken ct) =>
        Ok(ApiResponse<GoodsIssueDetail>.Success(await service.ConfirmAsync(id, request, ActorId(), ct), "Đã xác nhận xuất kho."));
    private int ActorId() => int.TryParse(User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        && id > 0 ? id : throw new UnauthorizedAccessException();
}
