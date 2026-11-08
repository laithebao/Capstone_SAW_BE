using SAW.Domain.Entities;

namespace SAW.Infrastructure.Repositories;

internal static class WholeLotFulfillment
{
    internal static string? Reason(OrderDetail line, IReadOnlyCollection<InventoryReservation> reservations,
        IReadOnlyCollection<Inventory> stocks, bool requirePicked = false)
    {
        if (line.RequestedProductBatchId is not { } batchId || line.ApprovedWeightKg <= 0)
            return "Dòng đơn chưa xác định nguyên lô được duyệt; cần quản lý kho rà soát.";
        var held = reservations.Where(r => r.ReleasedAt == null
            && r.ReservationStatus is "RESERVED" or "PARTIALLY_PICKED" or "PICKED").ToList();
        if (held.Count == 0 || held.Any(r => r.Inventory.ProductBatchId != batchId)
            || held.Sum(r => r.ReservedQuantity) != line.ApprovedWeightKg)
            return "Phần giữ hàng không còn đủ nguyên lô đã duyệt. Cần xử lý lại đơn và phần giữ hàng.";
        if (stocks.Any(i => i.ProductBatchId != batchId) || stocks.Sum(i => i.QuantityOnHand) != line.ApprovedWeightKg
            || stocks.Any(i => i.QuantityOnHand != held.Where(r => r.InventoryId == i.InventoryId).Sum(r => r.ReservedQuantity)
                || i.ReservedQuantity != held.Where(r => r.InventoryId == i.InventoryId).Sum(r => r.ReservedQuantity)))
            return "Tồn thực tế không khớp nguyên lô đã duyệt. Cần rà soát đơn và phần giữ hàng; không tự thay đổi khối lượng đơn.";
        if (held.Any(r => r.PickedQuantity < 0 || r.PickedQuantity > r.ReservedQuantity)
            || (requirePicked && held.Any(r => r.ReservationStatus != "PICKED" || r.PickedQuantity != r.ReservedQuantity)))
            return "Cần xác nhận đã soạn đủ nguyên lô trước khi lập hoặc xác nhận phiếu xuất.";
        return null;
    }
}
