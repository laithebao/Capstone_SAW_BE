using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAW.Application.Repositories;
using SAW.Infrastructure.Persistence;
namespace SAW.Infrastructure.Notifications;
public sealed class EmailOutboxWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<EmailOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (config.GetValue<bool>("Email:BusinessNotificationsEnabled"))
                    for (var i = 0; i < 20 && await SendNextAsync(ct); i++) { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning("Email outbox unavailable: {Type}", e.GetType().Name); }
            try { if (!await timer.WaitForNextTickAsync(ct)) break; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    public async Task<bool> SendNextAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Optional local-test restriction. Empty in normal deployments; never rewrites recipients.
        var allowed = config.GetSection("Email:AllowedRecipients").Get<string[]>() ?? [];
        var item = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = DateTime.UtcNow;
            var next = await db.EmailOutbox.FromSqlInterpolated($"SELECT * FROM EMAIL_OUTBOX WITH (UPDLOCK,READPAST,ROWLOCK) WHERE SentAt IS NULL AND Attempts < 5 AND NextAttemptAt <= {now}")
                .Where(x => allowed.Length == 0 || allowed.Contains(x.Recipient)).OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            if (next != null) { next.LeaseId = Guid.NewGuid(); next.Attempts++; next.NextAttemptAt = now.AddMinutes(5); await db.SaveChangesAsync(ct); }
            await tx.CommitAsync(ct); return next;
        });
        if (item is null) return false;
        try
        {
            var stillVerified = await db.Accounts.Where(VerifiedEmail.Eligible).AnyAsync(a => a.AccountId == item.AccountId && a.Email == item.Recipient, ct);
            if (!stillVerified) throw new InvalidOperationException("RecipientUnavailable");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var body = await WithLinkAsync(db, item, timeout.Token);
            await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendBusinessAsync(item.Recipient, item.Subject, body, $"saw-outbox-{item.Id}", timeout.Token);
            await db.EmailOutbox.Where(x => x.Id == item.Id && x.LeaseId == item.LeaseId).ExecuteUpdateAsync(s => s.SetProperty(x => x.SentAt, DateTime.UtcNow).SetProperty(x => x.LastError, (string?)null), ct);
            logger.LogInformation("Email {Id} ({EventKey}) accepted by {Sender} at attempt {Attempt}", item.Id, item.EventKey,
                scope.ServiceProvider.GetRequiredService<IEmailSender>().GetType().Name, item.Attempts);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            var retry = DateTime.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, item.Attempts)));
            var reason = e is InvalidOperationException { Message: "RecipientUnavailable" } ? "RecipientUnavailable" : e.GetType().Name;
            await db.EmailOutbox.Where(x => x.Id == item.Id && x.LeaseId == item.LeaseId).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, retry).SetProperty(x => x.LastError, reason), ct);
            logger.LogWarning("Email {Id} ({EventKey}) failed at attempt {Attempt}: {Reason}", item.Id, item.EventKey, item.Attempts, reason);
        }
        return true;
    }

    private async Task<string> WithLinkAsync(AppDbContext db, SAW.Domain.Entities.EmailOutbox item, CancellationToken ct)
    {
        if (!Uri.TryCreate(config["QrCode:PublicFrontendBaseUrl"], UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https")) return item.Body;
        var key = item.EventKey.Split(':');
        if (key.Length < 2 || !long.TryParse(key[1], out var id)) return item.Body;
        string? route = null;
        if (key[0] == "BATCH" && await db.ProductBatches.AnyAsync(x => x.ProductBatchId == id && x.Supplier.AccountId == item.AccountId, ct)) route = $"/supplier/batches/{id}/status";
        if (key[0] == "RECEIPT")
        {
            var batchId = await db.GoodsReceipts.Where(x => x.GoodsReceiptId == id && x.ProductBatch.Supplier.AccountId == item.AccountId).Select(x => (long?)x.ProductBatchId).SingleOrDefaultAsync(ct);
            if (batchId.HasValue) route = $"/supplier/batches/{batchId}/status";
        }
        if (key[0] == "ISSUE")
        {
            var orderId = await db.GoodsIssues.Where(x => x.GoodsIssueId == id && x.PurchaseOrder.Distributor.AccountId == item.AccountId).Select(x => (long?)x.PurchaseOrderId).SingleOrDefaultAsync(ct);
            if (orderId.HasValue) route = $"/distributor/orders/{orderId}";
        }
        return route is null ? item.Body : item.Body + "\nTra cứu: " + origin.AbsoluteUri.TrimEnd('/') + route;
    }
}
