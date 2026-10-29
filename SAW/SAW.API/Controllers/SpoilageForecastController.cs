using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.SpoilageForecast;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/warehouse/spoilage-forecast")]
[Authorize(Roles = "WAREHOUSE_MANAGER,ADMINISTRATOR")]
public sealed class SpoilageForecastController(ISpoilageForecastService forecastService) : ControllerBase
{
    /// <summary>
    /// POST api/warehouse/spoilage-forecast
    /// Tạo mới hoặc lấy cache phân tích dự báo hao hụt bằng Gemini AI cho một lô hàng.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> GenerateForecast([FromBody] SpoilageForecastRequest request, CancellationToken ct)
    {
        if (request.ProductBatchId <= 0)
        {
            return BadRequest(ApiResponse.BadRequest("Mã định danh lô hàng (ProductBatchId) không hợp lệ."));
        }

        var accountId = ActorId();
        var response = await forecastService.GenerateForecastAsync(accountId, request, ct);

        // Luôn trả 200 kèm flag IsError trong response để client xử lý mượt mà, không throw network error.
        return Ok(ApiResponse<SpoilageForecastResponse>.Success(response));
    }

    /// <summary>
    /// GET api/warehouse/spoilage-forecast/{batchId:long}/latest
    /// Lấy kết quả dự báo hao hụt gần nhất đã lưu trong cơ sở dữ liệu.
    /// </summary>
    [HttpGet("{batchId:long}/latest")]
    public async Task<IActionResult> GetLatestForecast(long batchId, CancellationToken ct)
    {
        if (batchId <= 0)
        {
            return BadRequest(ApiResponse.BadRequest("Mã định danh lô hàng không hợp lệ."));
        }

        var response = await forecastService.GetLatestForecastAsync(batchId, ct);
        if (response == null)
        {
            return NotFound(ApiResponse.NotFound("Chưa có dữ liệu dự báo hao hụt cho lô hàng này."));
        }

        return Ok(ApiResponse<SpoilageForecastResponse>.Success(response));
    }

    /// <summary>
    /// GET api/warehouse/spoilage-forecast/{batchId:long}/history
    /// Lịch sử các lần dự báo hao hụt của lô hàng.
    /// </summary>
    [HttpGet("{batchId:long}/history")]
    public async Task<IActionResult> GetForecastHistory(long batchId, [FromQuery] int count = 5, CancellationToken ct = default)
    {
        if (batchId <= 0)
        {
            return BadRequest(ApiResponse.BadRequest("Mã định danh lô hàng không hợp lệ."));
        }

        var response = await forecastService.GetForecastHistoryAsync(batchId, Math.Clamp(count, 1, 20), ct);
        return Ok(ApiResponse<IReadOnlyList<SpoilageForecastResponse>>.Success(response));
    }

    /// <summary>
    /// GET api/warehouse/spoilage-forecast/high-risk
    /// Danh sách các lô hàng đang lưu kho có nguy cơ hao hụt cao nhất (HIGH hoặc CRITICAL).
    /// </summary>
    [HttpGet("high-risk")]
    public async Task<IActionResult> GetHighRiskBatches([FromQuery] int limit = 10, CancellationToken ct = default)
    {
        var response = await forecastService.GetHighRiskBatchesAsync(Math.Clamp(limit, 1, 50), ct);
        return Ok(ApiResponse<IReadOnlyList<SpoilageForecastResponse>>.Success(response));
    }

    private int ActorId() =>
        int.TryParse(User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        && id > 0 ? id : throw new UnauthorizedAccessException();
}
