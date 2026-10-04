using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAW.Domain.Entities;
using SAW.Infrastructure.Persistence;
using SAW.Infrastructure.Repositories.Suppliers;

namespace SAW.API.Controllers;

[ApiController]
[Route("api/supplier-files")]
[Authorize(Roles = "SUPPLIER")]
public class SupplierFilesController(AppDbContext db, IConfiguration config, IWebHostEnvironment env) : ControllerBase
{
    private const long MaxBytes = 10 * 1024 * 1024;
    private string StorageRoot => Path.GetFullPath(config["SupplierFiles:StoragePath"] ??
        Path.Combine(env.ContentRootPath, "App_Data", "SupplierFiles"));
    private int AccountId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ??
        User.FindFirstValue("account_id") ?? User.FindFirstValue("AccountID") ?? "0");

    [HttpPost]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromQuery] string purpose = "DOCUMENT", CancellationToken ct = default)
    {
        purpose = purpose.ToUpperInvariant();
        if (AccountId <= 0) return Unauthorized();
        if (purpose is not ("DOCUMENT" or "AVATAR")) return BadRequest(new { message = "Mục đích upload không hợp lệ." });
        if (file.Length is <= 0 or > MaxBytes) return BadRequest(new { message = "File phải có dữ liệu và không vượt quá 10 MB." });
        await using var input = file.OpenReadStream();
        var header = new byte[12];
        var count = await input.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken: ct);
        input.Position = 0;
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var type = extension switch
        {
            ".pdf" when count >= 5 && header.AsSpan(0, 5).SequenceEqual("%PDF-"u8) => "application/pdf",
            ".jpg" or ".jpeg" when count >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255 => "image/jpeg",
            ".png" when count >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) => "image/png",
            ".webp" when count >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8) => "image/webp",
            _ => null
        };
        if (type is null || (purpose == "AVATAR" && type == "application/pdf") ||
            (purpose == "DOCUMENT" && type == "image/webp"))
            return BadRequest(new { message = "File không hợp lệ. Tài liệu: PDF/JPG/PNG; ảnh đại diện: JPG/PNG/WEBP." });
        var id = Guid.NewGuid();
        var storedName = id.ToString("N") + extension;
        Directory.CreateDirectory(StorageRoot);
        var path = Path.Combine(StorageRoot, storedName);
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, ct);
            var name = Path.GetFileName(file.FileName);
            db.Set<SupplierFile>().Add(new SupplierFile
            {
                SupplierFileId = id, AccountId = AccountId, Purpose = purpose,
                FileName = name[..Math.Min(name.Length, 255)], ContentType = type,
                StorageName = storedName, ByteLength = file.Length, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            System.IO.File.Delete(path);
            throw;
        }
        return Ok(new { id, url = SupplierFileLinks.Prefix + id + (purpose == "AVATAR" ? "/avatar" : ""), fileName = file.FileName });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var file = await db.Set<SupplierFile>().AsNoTracking().SingleOrDefaultAsync(f =>
            f.SupplierFileId == id && f.AccountId == AccountId, ct);
        if (file is null) return NotFound(new { message = "Không tìm thấy tài liệu thuộc tài khoản hiện tại." });
        var path = Path.Combine(StorageRoot, file.StorageName);
        return System.IO.File.Exists(path) ? PhysicalFile(path, file.ContentType, file.FileName) :
            NotFound(new { message = "File không còn trên nơi lưu trữ." });
    }

    // Only the currently linked avatar is public; documents always require the owning account.
    [AllowAnonymous]
    [HttpGet("{id:guid}/avatar")]
    public async Task<IActionResult> Avatar(Guid id, CancellationToken ct)
    {
        var url = SupplierFileLinks.Prefix + id + "/avatar";
        var file = await db.Set<SupplierFile>().AsNoTracking().SingleOrDefaultAsync(f =>
            f.SupplierFileId == id && f.Purpose == "AVATAR" && f.SupplierId != null &&
            db.Accounts.Any(a => a.AccountId == f.AccountId && a.AvatarUrl == url), ct);
        if (file is null) return NotFound();
        var path = Path.Combine(StorageRoot, file.StorageName);
        return System.IO.File.Exists(path) ? PhysicalFile(path, file.ContentType) : NotFound();
    }
}
