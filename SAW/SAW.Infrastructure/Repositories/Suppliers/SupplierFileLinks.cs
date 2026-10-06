using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.Suppliers.DTOs;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories.Suppliers;

public static class SupplierFileLinks
{
    public const string Prefix = "/api/supplier-files/";
    public static bool IsManaged(string url) => url.StartsWith(Prefix, StringComparison.Ordinal);
    public static bool TryId(string url, out Guid id) => Guid.TryParse(url.Split('/').LastOrDefault(), out id);

    public static async Task ReplaceDocuments(AppDbContext db, int accountId, int supplierId,
        long? batchId, List<string>? urls, CancellationToken ct)
    {
        if (urls is null) return;
        var ids = new List<Guid>();
        foreach (var url in urls.Distinct())
        {
            if (!IsManaged(url))
            {
                // Existing certification evidence remains readable; a batch only accepts owned uploads.
                if (batchId.HasValue) throw new ArgumentException("Tài liệu lô hàng phải được tải lên bằng tài khoản hiện tại.");
                var legacy = await db.SupplierCertifications.AnyAsync(c => c.SupplierId == supplierId && c.EvidenceFileUrl == url, ct);
                if (!legacy) throw new ArgumentException("Tài liệu không thuộc hồ sơ hiện tại.");
                continue;
            }
            if (!TryId(url, out var id)) throw new ArgumentException("ID tài liệu không hợp lệ.");
            ids.Add(id);
        }
        var files = await db.Set<SupplierFile>().Where(f => ids.Contains(f.SupplierFileId)).ToListAsync(ct);
        if (files.Count != ids.Count || files.Any(f => f.AccountId != accountId || f.Purpose != "DOCUMENT" ||
            (f.SupplierId.HasValue && (f.SupplierId != supplierId || f.ProductBatchId != batchId))))
            throw new ArgumentException("Tài liệu không thuộc tài khoản hoặc đang liên kết với bản ghi khác.");
        var old = await db.Set<SupplierFile>().Where(f => f.SupplierId == supplierId &&
            f.ProductBatchId == batchId && f.Purpose == "DOCUMENT").ToListAsync(ct);
        foreach (var f in old.Where(f => !ids.Contains(f.SupplierFileId)))
        { f.SupplierId = null; f.ProductBatchId = null; }
        foreach (var f in files) { f.SupplierId = supplierId; f.ProductBatchId = batchId; }
    }

    public static async Task SaveAvatar(AppDbContext db, int accountId, int supplierId, string? url, CancellationToken ct)
    {
        if (url is null) return;
        var account = await db.Accounts.SingleAsync(a => a.AccountId == accountId, ct);
        if (url == account.AvatarUrl) return;
        if (string.IsNullOrEmpty(url)) { account.AvatarUrl = null; return; }
        var clean = url.EndsWith("/avatar", StringComparison.Ordinal) ? url[..^7] : url;
        if (!IsManaged(clean) || !TryId(clean, out var id)) throw new ArgumentException("Ảnh đại diện phải được tải lên bằng tài khoản hiện tại.");
        var file = await db.Set<SupplierFile>().SingleOrDefaultAsync(f => f.SupplierFileId == id &&
            f.AccountId == accountId && f.Purpose == "AVATAR", ct)
            ?? throw new ArgumentException("Ảnh đại diện không thuộc tài khoản hiện tại.");
        file.SupplierId = supplierId;
        account.AvatarUrl = url;
    }

    public static Task<List<SupplierDocumentDto>> Documents(AppDbContext db, int supplierId, long? batchId, CancellationToken ct) =>
        db.Set<SupplierFile>().AsNoTracking().Where(f => f.SupplierId == supplierId &&
            f.ProductBatchId == batchId && f.Purpose == "DOCUMENT").OrderBy(f => f.CreatedAt)
        .Select(f => new SupplierDocumentDto
        {
            FileName = f.FileName, FileUrl = Prefix + f.SupplierFileId.ToString().ToLower(),
            FileType = f.ContentType, FileSizeMb = f.ByteLength / 1048576d
        }).ToListAsync(ct);
}
