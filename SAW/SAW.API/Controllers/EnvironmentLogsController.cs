using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.QcInspections;
using SAW.Domain.Common;
using System.Security.Claims;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/environment-logs")]
[Authorize]
public sealed class EnvironmentLogsController(IQcInspectionService service) : ControllerBase
{
    private int ActorId => int.Parse(
        User.FindFirstValue("account_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

    // =========================================================================
    // UC25 – Input Actual Storage Temperature
    // POST /api/environment-logs
    // =========================================================================

    [HttpPost]
    [Authorize(Roles = "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<EnvironmentLogDto>>> Create(
        [FromBody] CreateEnvironmentLogRequest request,
        CancellationToken ct)
    {
        var result = await service.CreateEnvironmentLogAsync(request, ActorId, ct);

        if (result.IsTempOutOfRange == true)
        {
            // Still created but include a warning
            return Created(
                $"api/environment-logs/{result.Id}",
                ApiResponse<EnvironmentLogDto>.Created(
                    result,
                    $"⚠ Nhiệt độ {result.TemperatureC}°C nằm ngoài ngưỡng bảo quản của lô hàng. " +
                    "Dữ liệu đã được ghi nhận."));
        }

        return Created(
            $"api/environment-logs/{result.Id}",
            ApiResponse<EnvironmentLogDto>.Created(result, "Ghi nhận nhiệt độ bảo quản thành công."));
    }

    // =========================================================================
    // In-Storage Cold Chain Monitoring
    // GET /api/environment-logs/in-storage-batches
    // =========================================================================

    [HttpGet("in-storage-batches")]
    [Authorize(Roles = "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<PagedResult<InStorageBatchEnvDto>>>> GetInStorageBatches(
        [FromQuery] InStorageBatchEnvQuery query,
        CancellationToken ct)
    {
        var result = await service.GetInStorageBatchesWithEnvAsync(query, ct);
        return Ok(ApiResponse<PagedResult<InStorageBatchEnvDto>>.Success(result));
    }

    // =========================================================================
    // GET /api/environment-logs/batches/{batchId}
    // =========================================================================

    [HttpGet("batches/{batchId:long}")]
    [Authorize(Roles = "QC_STAFF,WAREHOUSE_MANAGER,ADMINISTRATOR")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<EnvironmentLogDto>>>> GetBatchLogs(
        long batchId,
        CancellationToken ct)
    {
        var result = await service.GetBatchEnvironmentLogsAsync(batchId, ct);
        return Ok(ApiResponse<IReadOnlyList<EnvironmentLogDto>>.Success(result));
    }
}
