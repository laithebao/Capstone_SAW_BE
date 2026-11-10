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
    /// Tạo dự báo hao hụt cho toàn bộ lô hàng đang lưu kho còn tồn.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> GenerateForecasts(CancellationToken ct)
    {
        ActorId();
        var (forecasts, error) = await forecastService.GenerateForecastsAsync(ct);
        return error is null
            ? Ok(ApiResponse.Success(forecasts))
            : StatusCode(StatusCodes.Status502BadGateway,
                ApiResponse<object>.Failure(StatusCodes.Status502BadGateway, error));
    }

    [HttpGet]
    public async Task<IActionResult> GetLatestForecasts(CancellationToken ct)
    {
        var forecasts = await forecastService.GetLatestForecastsAsync(ct);
        return Ok(ApiResponse.Success(forecasts));
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
