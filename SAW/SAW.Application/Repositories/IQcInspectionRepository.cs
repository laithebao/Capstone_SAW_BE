using SAW.Domain.Entities;

namespace SAW.Application.Repositories;

public interface IQcInspectionRepository
{
    // ── Lookup ────────────────────────────────────────────────────────────────

    Task<ProductBatch?> GetBatchAsync(long batchId, CancellationToken ct);

    Task<InspectionStandardVersion?> GetVersionWithCriteriaAsync(
        long versionId, CancellationToken ct);

    Task<bool> HasActiveInspectionAsync(long batchId, CancellationToken ct);

    Task<bool> InspectionCodeExistsAsync(string code, CancellationToken ct);

    Task<QcInspection?> GetInspectionAsync(long id, CancellationToken ct);

    Task<QcInspection?> GetInspectionDetailAsync(long id, CancellationToken ct);

    Task<int> CountImagesAsync(long inspectionId, CancellationToken ct);

    Task<QualityImage?> GetImageAsync(long imageId, CancellationToken ct);

    Task<bool> WarehouseLocationExistsAsync(int locationId, CancellationToken ct);

    // ── Write ─────────────────────────────────────────────────────────────────

    void AddInspection(QcInspection inspection);

    void AddSensoryResult(SensoryResult result);

    void AddLabResult(LabResult result);

    void AddImage(QualityImage image);

    void RemoveImage(QualityImage image);

    void AddEnvironmentLog(EnvironmentLog log);

    // ── List (UC62) ───────────────────────────────────────────────────────────

    Task<(IReadOnlyList<QcInspection> Items, int TotalCount)> ListAsync(
        string? batchCode,
        string? inspectionCode,
        string? status,
        string? qcResult,
        int? qcAccountId,
        DateTime? fromDate,
        DateTime? toDate,
        int page,
        int pageSize,
        CancellationToken ct);

    // ── Save ──────────────────────────────────────────────────────────────────

    Task SaveChangesAsync(CancellationToken ct);

    Task<QcInspection> FinalizeAsync(long id, int actorAccountId, Action<QcInspection> evaluate, CancellationToken ct);
}
