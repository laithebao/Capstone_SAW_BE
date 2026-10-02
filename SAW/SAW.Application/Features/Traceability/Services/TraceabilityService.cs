using SAW.Application.Exceptions;
using SAW.Application.Features.QrCodes;
using SAW.Application.Features.Traceability.Dtos;
using SAW.Application.Features.Traceability.Interfaces;
using SAW.Application.Repositories;
using SAW.Domain.Entities;

namespace SAW.Application.Features.Traceability.Services;

public sealed class TraceabilityService(ITraceabilityRepository repository) : ITraceabilityService
{
    public const string NotFoundMessage = "Batch information not found.";
    public const string UnavailableMessage = "This batch is no longer available.";

    public static bool IsValidPublicToken(string? token) => token is { Length: 64 }
        && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<PublicTraceabilityResponse> GetAsync(string publicToken, CancellationToken ct)
    {
        if (!IsValidPublicToken(publicToken)) throw new NotFoundException(NotFoundMessage);
        var data = await repository.GetByPublicTokenAsync(publicToken, ct)
            ?? throw new NotFoundException(NotFoundMessage);

        // Reuse UC55's read policy, constructing only the fields used by that policy.
        var batch = new ProductBatch { BatchStatus = data.BatchStatus, QualityGrade = data.BatchQualityGrade };
        var qc = data.LatestQc is null ? null : new QcInspection
        {
            InspectionStatus = data.LatestQc.InspectionStatus, QcResult = data.LatestQc.Result,
            QualityGrade = data.LatestQc.Grade, StartedAt = data.LatestQc.StartedAt, CompletedAt = data.LatestQc.CompletedAt
        };
        if (!data.IsActive || !QrCodeEligibility.HasImage(new QrCode { QrImageUrl = data.QrImageUrl })
            || !QrCodeEligibility.CanRead(batch, qc)) throw new GoneException(UnavailableMessage);

        var milestones = new List<PublicTraceabilityMilestone>
        {
            new("QC_COMPLETED", "Hoàn tất kiểm định chất lượng", Utc(qc!.CompletedAt!.Value))
        };
        milestones.AddRange(data.ReceiptMilestones.Select(at => new PublicTraceabilityMilestone("RECEIVED", "Nhập kho", Utc(at))));
        milestones.AddRange(data.IssueMilestones.Select(at => new PublicTraceabilityMilestone("ISSUED", "Xuất kho", Utc(at))));
        var quality = PublicQcPresentation.Create(data.LatestQc!);

        return new(data.BatchCode, data.ProductName, data.CropTypeName, data.SupplierName,
            data.Origin, data.HarvestDate, data.ExpiryDate, quality,
            milestones.OrderBy(m => m.OccurredAt).ThenBy(m => m.Type, StringComparer.Ordinal).ToList());
    }

    // QC writes DateTime.UtcNow; SQL datetime2 does not retain DateTime.Kind.
    internal static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
