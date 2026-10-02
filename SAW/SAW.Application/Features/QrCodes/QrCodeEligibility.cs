using SAW.Domain.Entities;

namespace SAW.Application.Features.QrCodes;

public static class QrCodeEligibility
{
    public static readonly string[] CreationStatuses = ["APPROVED_FOR_STORAGE", "RECEIVED", "IN_STOCK"];
    public static readonly string[] ReadStatuses = [.. CreationStatuses, "RESERVED", "PARTIALLY_ISSUED", "ISSUED"];

    // Use the latest inspection of ANY status, never the latest PASS alone.
    public static bool HasAcceptedQc(ProductBatch batch, QcInspection? latest) =>
        latest is { InspectionStatus: "COMPLETED", QcResult: "PASS", CompletedAt: not null }
        && latest.CompletedAt >= latest.StartedAt
        && latest.QualityGrade is "A" or "B" or "C" or "D"
        && latest.QualityGrade == batch.QualityGrade;

    public static bool CanCreate(ProductBatch batch, QcInspection? latest) =>
        CreationStatuses.Contains(batch.BatchStatus) && HasAcceptedQc(batch, latest);

    public static bool CanRead(ProductBatch batch, QcInspection? latest) =>
        ReadStatuses.Contains(batch.BatchStatus) && HasAcceptedQc(batch, latest);

    public static bool HasImage(QrCode code) => Uri.TryCreate(code.QrImageUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == "https";
}
