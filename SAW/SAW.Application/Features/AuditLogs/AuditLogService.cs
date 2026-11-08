using System.Text;
using SAW.Application.Exceptions;
using SAW.Domain.Entities;

namespace SAW.Application.Features.AuditLogs;

public sealed record AuditLogListItem(
    long Id, int? AccountId, string ActorName, string? ActorRole, string ActionType,
    string EntityName, string? EntityId, string? Description, string Status,
    string? IpAddress, DateTime CreatedAt, bool HasChanges);

public sealed record AuditLogDetail(
    long Id, int? AccountId, string ActorName, string? ActorUsername, string? ActorEmail,
    string? ActorRole, string ActionType, string EntityName, string? EntityId,
    string? Description, string Status, string? IpAddress, string? UserAgent,
    DateTime CreatedAt, string? OldDataJson, string? NewDataJson);

public sealed record AuditLogListResponse(
    IReadOnlyList<AuditLogListItem> Items, int TotalCount, int Page, int PageSize);

public sealed record AuditLogQuery(
    string? Search, DateTime? From, DateTime? To, int? AccountId, string? Actor, string? ActionType,
    string? EntityName, string? Status, bool ChangesOnly, int Page = 1, int PageSize = 20);

public interface IAuditLogRepository
{
    Task<(IReadOnlyList<AuditLog> Items, int TotalCount)> SearchAsync(
        AuditLogQuery query, bool paginate, CancellationToken cancellationToken);
    Task<AuditLog?> GetByIdAsync(long id, CancellationToken cancellationToken);
}

public interface IAuditLogService
{
    Task<AuditLogListResponse> SearchAsync(AuditLogQuery query, CancellationToken cancellationToken);
    Task<AuditLogDetail> GetAsync(long id, CancellationToken cancellationToken);
    Task<byte[]> ExportCsvAsync(AuditLogQuery query, CancellationToken cancellationToken);
}

public sealed class AuditLogService(IAuditLogRepository repository) : IAuditLogService
{
    public async Task<AuditLogListResponse> SearchAsync(AuditLogQuery query, CancellationToken cancellationToken)
    {
        var normalized = query with { Page = Math.Max(1, query.Page), PageSize = Math.Clamp(query.PageSize, 1, 100) };
        var result = await repository.SearchAsync(normalized, true, cancellationToken);
        return new AuditLogListResponse(result.Items.Select(MapList).ToList(), result.TotalCount, normalized.Page, normalized.PageSize);
    }

    public async Task<AuditLogDetail> GetAsync(long id, CancellationToken cancellationToken)
    {
        var item = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("Không tìm thấy nhật ký hệ thống.");
        return MapDetail(item);
    }

    public async Task<byte[]> ExportCsvAsync(AuditLogQuery query, CancellationToken cancellationToken)
    {
        var result = await repository.SearchAsync(query, false, cancellationToken);
        var csv = new StringBuilder("Mã nhật ký\tThời gian\tNgười thực hiện\tVai trò\tHành động\tModule\tĐối tượng\tTrạng thái\tIP\tMô tả\r\n");
        foreach (var item in result.Items)
        {
            var row = MapList(item);
            csv.AppendLine(string.Join('\t', new[]
            {
                $"ALOG-{row.Id:D6}", row.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), row.ActorName,
                row.ActorRole, row.ActionType, row.EntityName, row.EntityId, row.Status,
                row.IpAddress, row.Description
            }.Select(ExcelCell)));
        }
        var preamble = Encoding.Unicode.GetPreamble();
        var content = Encoding.Unicode.GetBytes(csv.ToString());
        return [.. preamble, .. content];
    }

    private static string ExcelCell(string? value)
    {
        var safe = (value ?? string.Empty).Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        return safe.Length > 0 && "=+-@".Contains(safe[0]) ? $"'{safe}" : safe;
    }
    private static string Status(string action) => action.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
        action.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ? "FAILED" :
        action.Contains("WARN", StringComparison.OrdinalIgnoreCase) ? "WARNING" : "SUCCESS";

    private static AuditLogListItem MapList(AuditLog item) => new(
        item.AuditLogId, item.AccountId, item.Account?.FullName ?? "Hệ thống",
        item.Account?.Role?.RoleName, item.ActionType, item.EntityName, item.EntityId,
        item.Description, Status(item.ActionType), item.IpAddress, item.CreatedAt,
        !string.IsNullOrWhiteSpace(item.OldDataJson) || !string.IsNullOrWhiteSpace(item.NewDataJson));

    private static AuditLogDetail MapDetail(AuditLog item) => new(
        item.AuditLogId, item.AccountId, item.Account?.FullName ?? "Hệ thống",
        item.Account?.Username, item.Account?.Email, item.Account?.Role?.RoleName,
        item.ActionType, item.EntityName, item.EntityId, item.Description, Status(item.ActionType),
        item.IpAddress, item.UserAgent, item.CreatedAt, item.OldDataJson, item.NewDataJson);
}
