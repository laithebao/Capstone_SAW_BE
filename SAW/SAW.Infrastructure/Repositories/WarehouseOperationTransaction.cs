using System.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SAW.Application.Exceptions;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

internal static class WarehouseOperationTransaction
{
    internal static async Task<T> ExecuteAsync<T>(DbContextOptions<AppDbContext> options, IHttpContextAccessor accessor,
        int actorId, Func<AppDbContext, Task<T>> action, CancellationToken ct)
    {
        await using var strategy = new AppDbContext(options, accessor);
        return await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // Fresh context on each SQL retry; no stale tracked entity survives a rollback.
            await using var db = new AppDbContext(options, accessor);
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_set_session_context @key=N'AccountID', @value={actorId}", ct);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                // Shared short SQL write gate, also used by capacity and order approval writers.
                // Durable workflow holds are separate; no transaction stays open awaiting a person.
                await db.Database.ExecuteSqlRawAsync("SELECT COUNT_BIG(*) FROM INVENTORY WITH (TABLOCKX,HOLDLOCK)", ct);
                var result = await action(db);
                await transaction.CommitAsync(ct);
                return result;
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("EXEC sys.sp_set_session_context @key=N'AccountID', @value=NULL", CancellationToken.None);
            }
        });
    }

    internal static async Task<ProductBatch> LockBatchAsync(AppDbContext db, long id, CancellationToken ct) =>
        await db.ProductBatches.FromSqlInterpolated($"SELECT * FROM PRODUCT_BATCH WITH (UPDLOCK,HOLDLOCK) WHERE ProductBatchID={id}")
            .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Không tìm thấy lô hàng.");

    internal static Task<List<Inventory>> LockInventoryAsync(AppDbContext db, long batchId, CancellationToken ct) =>
        db.Inventories.FromSqlInterpolated($"SELECT * FROM INVENTORY WITH (UPDLOCK,HOLDLOCK) WHERE ProductBatchID={batchId}")
            .OrderBy(i => i.InventoryId).ToListAsync(ct);

}
