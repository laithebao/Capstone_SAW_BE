using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Persistence.Configurations;

public class InventoryReservationConfiguration : IEntityTypeConfiguration<InventoryReservation>
{
    public void Configure(EntityTypeBuilder<InventoryReservation> builder)
    {
        builder.ToTable("INVENTORY_RESERVATION");
        builder.HasKey(x => x.InventoryReservationId);
        builder.Property(x => x.InventoryReservationId).HasColumnName("InventoryReservationID").UseIdentityColumn();
        builder.Property(x => x.OrderDetailId).HasColumnName("OrderDetailID").IsRequired();
        builder.Property(x => x.InventoryId).HasColumnName("InventoryID").IsRequired();
        builder.Property(x => x.ReservedQuantity).HasColumnName("ReservedQuantity").HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.PickedQuantity).HasColumnName("PickedQuantity").HasPrecision(18, 3).HasDefaultValue(0m);
        builder.Property(x => x.ReservationStatus).HasColumnName("ReservationStatus").HasMaxLength(25).HasDefaultValue("RESERVED");
        builder.Property(x => x.ReservedAt).HasColumnName("ReservedAt").HasDefaultValueSql("SYSDATETIME()");
        builder.Property(x => x.ReleasedAt).HasColumnName("ReleasedAt");

        builder.HasIndex(x => new { x.OrderDetailId, x.InventoryId })
               .IsUnique().HasDatabaseName("UQ_INVENTORY_RESERVATION_OrderInventory");

        builder.HasOne(x => x.OrderDetail)
               .WithMany(od => od.InventoryReservations)
               .HasForeignKey(x => x.OrderDetailId)
               .HasConstraintName("FK_INVENTORY_RESERVATION_ORDER_DETAIL");

        builder.HasOne(x => x.Inventory)
               .WithMany(i => i.Reservations)
               .HasForeignKey(x => x.InventoryId)
               .HasConstraintName("FK_INVENTORY_RESERVATION_INVENTORY");
    }
}
