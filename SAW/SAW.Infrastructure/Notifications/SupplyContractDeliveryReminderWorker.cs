using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAW.Application.Features.SupplyContracts;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Notifications;

/// <summary>Notifies suppliers and warehouse managers about delivery progress and missed periods.</summary>
public sealed class SupplyContractDeliveryReminderWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<SupplyContractDeliveryReminderWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CreateDueRemindersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Không thể kiểm tra lịch nhắc giao lô theo hợp đồng.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task CreateDueRemindersAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        var contracts = await db.SupplyContracts.AsNoTracking()
            .Where(contract => contract.ContractStatus == "APPROVED"
                && contract.StartDate <= today
                && contract.EndDate >= today)
            .Select(contract => new
            {
                contract.SupplyContractId,
                contract.ContractCode,
                SupplierAccountId = contract.Supplier.AccountId,
                contract.Supplier.SupplierName,
                contract.WarehouseManagerAccountId,
                contract.DeliveryQuantityKg,
                contract.StartDate,
                contract.EndDate,
                contract.DeliveryPeriod
            })
            .ToListAsync(ct);

        foreach (var contract in contracts)
        {
            (DateOnly Start, DateOnly End, decimal DeliveredKg, decimal RemainingKg)? latestOverdue = null;
            (DateOnly Start, DateOnly End, decimal DeliveredKg, decimal RemainingKg)? currentPeriod = null;
            var periodStart = contract.StartDate;

            while (periodStart <= contract.EndDate)
            {
                var periodEnd = SupplyContractQuotaPeriod.GetBounds(
                    contract.StartDate, contract.EndDate, contract.DeliveryPeriod, periodStart).End;
                if (periodStart > today) break;

                var isOverdue = periodEnd < today;
                var isCurrent = periodStart <= today && periodEnd >= today;
                if (isOverdue || isCurrent)
                {
                    // Count goods physically received and confirmed by warehouse, not merely declared batches.
                    var deliveredKg = await db.GoodsReceipts.AsNoTracking()
                        .Where(receipt => receipt.ReceiptStatus == "COMMITTED"
                            && receipt.ProductBatch.SupplyContractId == contract.SupplyContractId
                            && receipt.ProductBatch.ExpectedDeliveryDate >= periodStart
                            && receipt.ProductBatch.ExpectedDeliveryDate <= periodEnd)
                        .SumAsync(receipt => (decimal?)receipt.WeightInKg, ct) ?? 0m;
                    var remainingKg = Math.Max(0m, contract.DeliveryQuantityKg - deliveredKg);

                    if (remainingKg > 0m)
                    {
                        var period = (periodStart, periodEnd, deliveredKg, remainingKg);
                        if (isOverdue) latestOverdue = period;
                        else currentPeriod = period;
                    }
                }

                if (isCurrent) break;
                if (periodEnd >= contract.EndDate) break;
                periodStart = periodEnd.AddDays(1);
            }

            if (latestOverdue is { } overdue)
            {
                var key = $"{contract.SupplyContractId}:{overdue.End:yyyyMMdd}";
                var progress = FormatProgress(contract.DeliveryQuantityKg, overdue.DeliveredKg, overdue.RemainingKg);
                var daysLate = Math.Max(1, today.DayNumber - overdue.End.DayNumber);
                var supplierMessage = $"Hợp đồng {contract.ContractCode}: kỳ giao {overdue.Start:dd/MM/yyyy}–{overdue.End:dd/MM/yyyy} đã quá hạn {daysLate} ngày. {progress} Vui lòng liên hệ Quản lý kho để xử lý.";
                var managerMessage = $"Hợp đồng {contract.ContractCode} của Nhà cung cấp {contract.SupplierName}: kỳ giao {overdue.Start:dd/MM/yyyy}–{overdue.End:dd/MM/yyyy} đã quá hạn {daysLate} ngày. {progress} Vui lòng kiểm tra và làm việc với Nhà cung cấp.";

                await UpsertNotificationAsync(db, contract.SupplierAccountId,
                    "SUPPLY_CONTRACT_DELIVERY_OVERDUE", "Hợp đồng đã quá hạn giao lô",
                    supplierMessage, key, ct);
                await UpsertNotificationAsync(db, contract.WarehouseManagerAccountId,
                    "SUPPLY_CONTRACT_DELIVERY_OVERDUE", "Hợp đồng nhà cung cấp quá hạn giao",
                    managerMessage, key, ct);
            }

            if (currentPeriod is { } due)
            {
                var key = $"{contract.SupplyContractId}:{due.End:yyyyMMdd}";
                var daysLeft = due.End.DayNumber - today.DayNumber;
                var deadline = daysLeft == 0 ? "đến hạn giao hôm nay" : $"còn {daysLeft} ngày đến hạn giao";
                var title = daysLeft == 0
                    ? "Đến hạn giao lô hàng hôm nay"
                    : $"Còn {daysLeft} ngày đến hạn giao lô hàng";
                var message = $"Hợp đồng {contract.ContractCode}: kỳ giao {due.Start:dd/MM/yyyy}–{due.End:dd/MM/yyyy} {deadline} ({due.End:dd/MM/yyyy}). {FormatProgress(contract.DeliveryQuantityKg, due.DeliveredKg, due.RemainingKg)} Vui lòng khai báo và giao phần còn thiếu đúng hạn.";
                await UpsertNotificationAsync(db, contract.SupplierAccountId,
                    "SUPPLY_CONTRACT_DELIVERY_REMINDER", title,
                    message, key, ct);
            }
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
    }

    private static string FormatProgress(decimal committedKg, decimal deliveredKg, decimal remainingKg)
    {
        var culture = CultureInfo.GetCultureInfo("vi-VN");
        return $"Sản lượng đã giao và được kho xác nhận: {deliveredKg.ToString("#,##0.###", culture)}/{committedKg.ToString("#,##0.###", culture)} kg; còn cần giao {remainingKg.ToString("#,##0.###", culture)} kg.";
    }

    private static async Task UpsertNotificationAsync(
        AppDbContext db,
        int accountId,
        string type,
        string title,
        string message,
        string relatedId,
        CancellationToken ct)
    {
        var notification = await db.Notifications.FirstOrDefaultAsync(item =>
            item.AccountId == accountId
            && item.RelatedEntityType == type
            && item.RelatedEntityId == relatedId, ct);

        if (notification is null)
        {
            db.Notifications.Add(new Notification
            {
                AccountId = accountId,
                NotificationType = type,
                Priority = "HIGH",
                Title = title,
                Message = message,
                RelatedEntityType = type,
                RelatedEntityId = relatedId,
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            });
            return;
        }

        // Refresh the progress snapshot without changing whether the recipient has read it.
        notification.Title = title;
        notification.Message = message;
    }
}
