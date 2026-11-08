using Microsoft.EntityFrameworkCore;
using SAW.Application.Features.Traceability.Dtos;
using SAW.Application.Repositories;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Repositories;

public sealed class TraceabilityRepository(AppDbContext db) : ITraceabilityRepository
{
    public Task<TraceabilityData?> GetByPublicTokenAsync(string publicToken, CancellationToken ct) =>
        db.QrCodes.AsNoTracking().Where(q => q.PublicToken == publicToken && q.PackageCode == null)
            .AsSingleQuery()
            .Select(q => new TraceabilityData
            {
                IsActive = q.IsActive,
                QrImageUrl = q.QrImageUrl,
                BatchStatus = q.ProductBatch.BatchStatus,
                BatchQualityGrade = q.ProductBatch.QualityGrade,
                LatestQc = q.ProductBatch.QcInspections.OrderByDescending(i => i.StartedAt)
                    .ThenByDescending(i => i.QcInspectionId)
                    .Select(i => new TraceabilityQcEvidence(i.InspectionStatus, i.QcResult, i.QualityGrade, i.StartedAt, i.CompletedAt)
                    {
                        Standard = new(i.InspectionStandardVersion.InspectionStandardSet.StandardCode,
                            i.InspectionStandardVersion.InspectionStandardSet.StandardName, i.InspectionStandardVersion.VersionNo),
                        SamplingRatio = i.SamplingRatio,
                        SampleSize = i.SampleSize,
                        Criteria = i.InspectionStandardVersion.Criteria.OrderBy(c => c.CriterionGroup).ThenBy(c => c.CriterionCode)
                            .Select(c => new TraceabilityCriterionEvidence(c.CriterionCode, c.CriterionName, c.CriterionGroup,
                                c.DataType, c.Unit, c.IsRequired,
                                c.ResultDetails.Where(d => d.QcInspectionId == i.QcInspectionId)
                                    .Select(d => new TraceabilityCriterionResult(d.NumericValue, d.TextValue, d.BooleanValue, d.EvaluatedGrade, d.IsPassed))
                                    .FirstOrDefault(),
                                c.GradeRules.OrderBy(r => r.Grade).Select(r => new PublicTraceabilityGradeRule(
                                    r.Grade, r.MinValue, r.MaxValue, r.RequiredTextValue, r.IsFailRule)).ToList())).ToList()
                    })
                    .FirstOrDefault(),
                BatchCode = q.ProductBatch.BatchCode,
                ProductName = q.ProductBatch.ProductName,
                CropTypeName = q.ProductBatch.CropType.CropName,
                SupplierName = q.ProductBatch.Supplier.SupplierName,
                Origin = new(q.ProductBatch.GrowingArea.AreaName, q.ProductBatch.GrowingArea.Region, q.ProductBatch.GrowingArea.Province),
                HarvestDate = q.ProductBatch.HarvestDate,
                ExpiryDate = q.ProductBatch.ExpiryDate,
                ReceiptMilestones = q.ProductBatch.GoodsReceipts.Where(r => r.ReceiptStatus == "COMMITTED" && r.CommittedAt != null)
                    .Select(r => r.CommittedAt!.Value).Distinct().OrderBy(at => at).ToList(),
                IssueMilestones = q.ProductBatch.Inventories.SelectMany(i => i.GoodsIssueDetails)
                    .Where(d => d.GoodsIssue.IssueStatus == "COMMITTED" && d.GoodsIssue.CommittedAt != null)
                    .Select(d => d.GoodsIssue.CommittedAt!.Value).Distinct().OrderBy(at => at).ToList()
            }).SingleOrDefaultAsync(ct);
}
