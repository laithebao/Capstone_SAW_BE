using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class AiRiskForecastConfiguration : IEntityTypeConfiguration<AiRiskForecast>
{
    public void Configure(EntityTypeBuilder<AiRiskForecast> builder)
    {
        builder.ToTable("AI_RISK_FORECAST");
        builder.HasKey(x => x.AiRiskForecastId);
        builder.Property(x => x.AiRiskForecastId).HasColumnName("AIRiskForecastID").UseIdentityColumn();
        builder.Property(x => x.ProductBatchId).HasColumnName("ProductBatchID").IsRequired();
        builder.Property(x => x.SpoilageRiskScore).HasColumnName("SpoilageRiskScore").HasPrecision(6, 2).IsRequired();
        builder.Property(x => x.PredictedLossPercent).HasColumnName("PredictedLossPercent").HasPrecision(6, 2);
        builder.Property(x => x.SafeStorageRemainingDays).HasColumnName("SafeStorageRemainingDays");
        builder.Property(x => x.RiskLevel).HasColumnName("RiskLevel").HasMaxLength(20).IsRequired();
        builder.Property(x => x.ForecastHorizonDays).HasColumnName("ForecastHorizonDays");
        builder.Property(x => x.Recommendation).HasColumnName("Recommendation").HasMaxLength(1500);
        builder.Property(x => x.ModelVersion).HasColumnName("ModelVersion").HasMaxLength(100);
        builder.Property(x => x.InputSnapshotJson).HasColumnName("InputSnapshotJson").HasColumnType("nvarchar(max)");
        builder.Property(x => x.GeneratedAt).HasColumnName("GeneratedAt").HasDefaultValueSql("SYSDATETIME()");

        builder.HasOne(x => x.ProductBatch)
               .WithMany(b => b.AiRiskForecasts)
               .HasForeignKey(x => x.ProductBatchId)
               .HasConstraintName("FK_AI_RISK_FORECAST_BATCH");
    }
}
