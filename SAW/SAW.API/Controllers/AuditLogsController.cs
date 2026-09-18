using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SAW.Application.Features.AuditLogs;
using SAW.Domain.Common;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/admin/audit-logs")]
[Authorize(Roles = "ADMINISTRATOR")]
public sealed class AuditLogsController(IAuditLogService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<AuditLogListResponse>>> Search(
        [FromQuery] string? search, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int? accountId, [FromQuery] string? actor, [FromQuery] string? actionType, [FromQuery] string? entityName,
        [FromQuery] string? status, [FromQuery] bool changesOnly = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Ok(ApiResponse<AuditLogListResponse>.Success(await service.SearchAsync(
            new AuditLogQuery(search, from, to, accountId, actor, actionType, entityName, status, changesOnly, page, pageSize), cancellationToken)));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ApiResponse<AuditLogDetail>>> Get(long id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<AuditLogDetail>.Success(await service.GetAsync(id, cancellationToken)));

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string? search, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int? accountId, [FromQuery] string? actor, [FromQuery] string? actionType, [FromQuery] string? entityName,
        [FromQuery] string? status, [FromQuery] bool changesOnly = false, CancellationToken cancellationToken = default)
    {
        var bytes = await service.ExportCsvAsync(
            new AuditLogQuery(search, from, to, accountId, actor, actionType, entityName, status, changesOnly), cancellationToken);
        return File(bytes, "text/tab-separated-values; charset=utf-16", $"audit-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }
}
