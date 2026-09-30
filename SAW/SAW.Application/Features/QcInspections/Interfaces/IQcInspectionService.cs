namespace SAW.Application.Features.QcInspections;

public interface IQcInspectionService
{
    // UC20 – Create Inspection Form
    Task<QcInspectionDto> CreateAsync(
        CreateQcInspectionRequest request,
        int actorAccountId,
        CancellationToken ct);

    // UC21 – Declare Batch Sampling Ratio
    Task UpdateSamplingRatioAsync(
        long inspectionId,
        UpdateSamplingRatioRequest request,
        int actorAccountId,
        string actorRole,
        CancellationToken ct);

    // UC22 – Input Sensory Inspection Result
    Task SaveSensoryResultAsync(
        long inspectionId,
        SaveSensoryResultRequest request,
        int actorAccountId,
        string actorRole,
        CancellationToken ct);

    // UC23 – Upload Quality Evidence Image (metadata saved after Cloudinary upload)
    Task<QualityImageDto> AddImageAsync(
        long inspectionId,
        UploadQualityImageRequest request,
        int actorAccountId,
        string actorRole,
        CancellationToken ct);

    // UC23 – Delete Image
    Task DeleteImageAsync(
        long inspectionId,
        long imageId,
        int actorAccountId,
        string actorRole,
        CancellationToken ct);

    // UC24 – Input Laboratory Test Result
    Task SaveLabResultAsync(
        long inspectionId,
        SaveLabResultRequest request,
        int actorAccountId,
        string actorRole,
        CancellationToken ct);

    // UC22b – Input Environment Criteria Result
    Task SaveEnvironmentCriteriaAsync(
        long inspectionId,
        SaveEnvironmentCriteriaRequest request,
        int actorAccountId,
        string actorRole,
        CancellationToken ct);

    // UC25 – Input Actual Storage Temperature
    Task<EnvironmentLogDto> CreateEnvironmentLogAsync(
        CreateEnvironmentLogRequest request,
        int actorAccountId,
        CancellationToken ct);

    // UC52 + UC53 – Compare with Rule Set & Classify Grade (combined finalize)
    Task<FinalizeQcResultDto> FinalizeAsync(
        long inspectionId,
        int actorAccountId,
        string actorRole,
        CancellationToken ct);

    // UC54 – Reject Batch with Serious Defect (manual override)
    Task RejectBatchAsync(
        long inspectionId,
        RejectBatchRequest request,
        int actorAccountId,
        CancellationToken ct);

    // UC62 – View Inspection List
    Task<PagedResult<QcInspectionListItem>> ListAsync(
        QcInspectionListQuery query,
        int actorAccountId,
        string actorRole,
        CancellationToken ct);

    // Detail view
    Task<QcInspectionDetailDto> GetByIdAsync(
        long inspectionId,
        int actorAccountId,
        string actorRole,
        CancellationToken ct);
}
