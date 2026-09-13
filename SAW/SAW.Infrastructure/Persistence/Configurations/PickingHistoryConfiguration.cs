using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class PickingHistoryConfiguration : IEntityTypeConfiguration<PickingHistory>
{
    public void Configure(EntityTypeBuilder<PickingHistory> builder)
    {
        builder.ToTable("PICKING_HISTORY");
        builder.HasKey(x => x.PickingHistoryId);
        builder.Property(x => x.PickingHistoryId).HasColumnName("PickingHistoryID").UseIdentityColumn();
        builder.Property(x => x.InventoryReservationId).HasColumnName("InventoryReservationID").IsRequired();
        builder.Property(x => x.PickedByAccountId).HasColumnName("PickedByAccountID").IsRequired();
        builder.Property(x => x.PickedQuantity).HasColumnName("PickedQuantity").HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.PickedAt).HasColumnName("PickedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.Note).HasColumnName("Note").HasMaxLength(1000);

        builder.HasOne(x => x.InventoryReservation)
               .WithMany(r => r.PickingHistories)
               .HasForeignKey(x => x.InventoryReservationId)
               .HasConstraintName("FK_PICKING_HISTORY_RESERVATION");

        builder.HasOne(x => x.PickedByAccount)
               .WithMany()
               .HasForeignKey(x => x.PickedByAccountId)
               .HasConstraintName("FK_PICKING_HISTORY_ACCOUNT");
    }
}
