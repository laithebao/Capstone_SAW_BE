using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SAW.Application.Exceptions;
using SAW.Application.Features.QrCodes.Dtos;
using SAW.Application.Features.QrCodes.Interfaces;
using SAW.Application.Repositories;

namespace SAW.Application.Features.QrCodes.Services;

public sealed class ProductBatchQrCodeService(
    IQrCodeRepository repository, IQrCodeRenderer renderer, IQrImageStorage storage,
    QrCodeOptions options, ILogger<ProductBatchQrCodeService> logger) : IProductBatchQrCodeService
{
    public async Task<ProductBatchQrCodeResponse> GetAsync(long batchId, CancellationToken ct)
    {
        var data = await repository.GetAsync(batchId, ct)
            ?? throw new NotFoundException("Không tìm thấy lô hàng.");
        var code = data.Codes.FirstOrDefault();
        var disabled = data.Codes.Any(q => !q.IsActive);
        if (disabled || (code is not null && !QrCodeEligibility.CanRead(data.Batch, data.LatestInspection)))
            return new(batchId, data.Batch.BatchCode, "UNAVAILABLE", "QR đã bị vô hiệu hóa hoặc lô không còn được phép sử dụng QR.");
        if (code is not null && QrCodeEligibility.HasImage(code))
            return new(batchId, data.Batch.BatchCode, "READY", "QR đã sẵn sàng.", code.PublicToken,
                code.TraceabilityUrl, code.QrImageUrl, code.GeneratedAt);
        if (QrCodeEligibility.CanCreate(data.Batch, data.LatestInspection))
            return new(batchId, data.Batch.BatchCode, "PENDING", "Lô đủ điều kiện. Hệ thống đang chờ tạo hoặc phục hồi ảnh QR.");
        return new(batchId, data.Batch.BatchCode, code is null ? "INELIGIBLE" : "UNAVAILABLE",
            "Lô chưa đủ điều kiện tạo QR: cần QC hoàn tất, đạt chất lượng và trạng thái được chấp nhận.");
    }

    public async Task GenerateAsync(long batchId, CancellationToken ct)
    {
        if (!options.HasValidPublicUrl || !storage.IsConfigured) return;
        var data = await repository.GetAsync(batchId, ct);
        if (data is null || !QrCodeEligibility.CanCreate(data.Batch, data.LatestInspection)
            || data.Codes.Any(q => !q.IsActive || QrCodeEligibility.HasImage(q))) return;

        var existing = data.Codes.FirstOrDefault();
        var token = existing?.PublicToken ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var url = existing?.TraceabilityUrl ?? $"{options.PublicFrontendBaseUrl.TrimEnd('/')}/trace/{token}";
        var png = renderer.RenderPng(url);
        var assetId = $"saw-qr/{Guid.NewGuid():N}";
        string? imageUrl = null;
        var preserveAsset = false;
        try
        {
            imageUrl = await storage.UploadAsync(assetId, png, ct);
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || imageUrl.Length > 1000)
                throw new InvalidOperationException("QR storage returned an invalid HTTPS image URL.");
            var winner = await repository.SaveGeneratedAsync(batchId, token, url, imageUrl, ct);
            preserveAsset = winner?.QrImageUrl == imageUrl;
        }
        catch
        {
            // A commit acknowledgement can fail after SQL actually committed. Never delete
            // an image until a fresh read proves it is not referenced by the database.
            if (imageUrl is not null)
            {
                using var checkTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { preserveAsset = await repository.IsImageReferencedAsync(imageUrl, checkTimeout.Token); }
                catch
                {
                    preserveAsset = true;
                    logger.LogError("UC55 cannot verify commit for asset {AssetId}; retain it for reconciliation", assetId);
                }
            }
            throw;
        }
        finally
        {
            if (!preserveAsset)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await CleanupAsync(assetId, cleanupTimeout.Token);
            }
        }
    }

    private async Task CleanupAsync(string assetId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { await storage.DeleteAsync(assetId, ct); return; }
            catch
            {
                if (attempt == 2 || ct.IsCancellationRequested) break;
                try { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        logger.LogError("UC55 could not clean up its unused asset {AssetId}; manual reconciliation required", assetId);
    }
}
