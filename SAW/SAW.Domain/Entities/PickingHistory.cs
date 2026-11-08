namespace SAW.Domain.Entities;

public class PickingHistory
{
    public long PickingHistoryId { get; set; }
    public long InventoryReservationId { get; set; }
    public int PickedByAccountId { get; set; }

    public decimal PickedQuantity { get; set; }
    public DateTime PickedAt { get; set; }
    public string? Note { get; set; }

    // Navigation
    public InventoryReservation InventoryReservation { get; set; } = default!;
    public Account PickedByAccount { get; set; } = default!;
}
