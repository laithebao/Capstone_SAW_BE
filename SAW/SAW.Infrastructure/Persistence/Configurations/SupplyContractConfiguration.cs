using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public sealed class SupplyContractConfiguration : IEntityTypeConfiguration<SupplyContract>
{
    public void Configure(EntityTypeBuilder<SupplyContract> builder)
    {
        builder.ToTable("SUPPLY_CONTRACT", table =>
        {
            table.HasCheckConstraint("CK_SUPPLY_CONTRACT_QualityGrade",
                "[QualityGrade] IN ('A','B','C','D','E')");
            table.HasCheckConstraint("CK_SUPPLY_CONTRACT_DeliveryQuantity",
                "[DeliveryQuantityKg] > 0");
            table.HasCheckConstraint("CK_SUPPLY_CONTRACT_DeliveryPeriod",
                "[DeliveryPeriod] IN ('WEEK','MONTH')");
            table.HasCheckConstraint("CK_SUPPLY_CONTRACT_UnitPrice",
                "[UnitPriceVndPerKg] > 0");
            table.HasCheckConstraint("CK_SUPPLY_CONTRACT_DateRange",
                "[EndDate] >= [StartDate]");
            table.HasCheckConstraint("CK_SUPPLY_CONTRACT_Status",
                "[ContractStatus] IN ('PENDING_SUPPLIER','SUPPLIER_REJECTED','PENDING_ADMIN','ADMIN_REJECTED','APPROVED','CANCELLATION_REQUESTED','PENDING_ADMIN_CANCELLATION','CANCELLATION_REJECTED','CANCELLED')");
        });

        builder.HasKey(x => x.SupplyContractId);
        builder.Property(x => x.SupplyContractId).HasColumnName("SupplyContractID").UseIdentityColumn();
        builder.Property(x => x.ContractCode).HasMaxLength(40).IsRequired();
        builder.Property(x => x.WarehouseManagerAccountId).HasColumnName("WarehouseManagerAccountID").IsRequired();
        builder.Property(x => x.SupplierId).HasColumnName("SupplierID").IsRequired();
        builder.Property(x => x.CropTypeId).HasColumnName("CropTypeID").IsRequired();
        builder.Property(x => x.QualityGrade).HasMaxLength(5);
        builder.Property(x => x.DeliveryQuantityKg).HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.DeliveryPeriod).HasMaxLength(10).IsRequired();
        builder.Property(x => x.UnitPriceVndPerKg).HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.StartDate).IsRequired();
        builder.Property(x => x.EndDate).IsRequired();
        builder.Property(x => x.Terms).HasMaxLength(2000);
        builder.Property(x => x.AttachmentFileName).HasMaxLength(255);
        builder.Property(x => x.AttachmentStorageName).HasMaxLength(100);
        builder.Property(x => x.AttachmentContentType).HasMaxLength(100);
        builder.Property(x => x.ContractStatus).HasMaxLength(30).HasDefaultValue("PENDING_SUPPLIER");
        builder.Property(x => x.Revision).HasDefaultValue(1);
        builder.Property(x => x.SupplierRejectionReason).HasMaxLength(1000);
        builder.Property(x => x.AdminAccountId).HasColumnName("AdminAccountID");
        builder.Property(x => x.AdminRejectionReason).HasMaxLength(1000);
        builder.Property(x => x.CancellationRequestReason).HasMaxLength(1000);
        builder.Property(x => x.CancellationReason).HasMaxLength(1000);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("SYSDATETIME()");

        builder.HasIndex(x => x.ContractCode).IsUnique().HasDatabaseName("UQ_SUPPLY_CONTRACT_Code");
        builder.HasIndex(x => new { x.SupplierId, x.ContractStatus }).HasDatabaseName("IX_SUPPLY_CONTRACT_Supplier_Status");
        builder.HasIndex(x => new { x.WarehouseManagerAccountId, x.ContractStatus }).HasDatabaseName("IX_SUPPLY_CONTRACT_Warehouse_Status");

        builder.HasOne(x => x.WarehouseManagerAccount)
            .WithMany()
            .HasForeignKey(x => x.WarehouseManagerAccountId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_SUPPLY_CONTRACT_WAREHOUSE_MANAGER");

        builder.HasOne(x => x.Supplier)
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_SUPPLY_CONTRACT_SUPPLIER");

        builder.HasOne(x => x.CropType)
            .WithMany()
            .HasForeignKey(x => x.CropTypeId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_SUPPLY_CONTRACT_CROP_TYPE");

        builder.HasOne(x => x.AdminAccount)
            .WithMany()
            .HasForeignKey(x => x.AdminAccountId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_SUPPLY_CONTRACT_ADMIN");
    }
}
