# Trạng Thái Lô Hàng — Nhà Phân Phối Cần Biết

## Lô hàng nào nhà phân phối có thể thấy và mua?
Chỉ những lô đã **PASS kiểm định QC** mới hiển thị. Cụ thể, lô đang ở một trong 3 trạng thái sau:

| Trạng thái | Ý nghĩa thực tế |
|---|---|
| **APPROVED_FOR_STORAGE** | Đã qua QC và được duyệt nhập kho — hàng có thể đặt mua |
| **RECEIVED** | Hàng đã có mặt vật lý trong kho — sẵn sàng |
| **IN_STOCK** | Hàng đã nhập kho đầy đủ — sẵn sàng giao ngay |

## Tại sao một lô bỗng nhiên biến mất khỏi danh sách?
Lô đang hiển thị có thể biến mất do:
- **Đã được đặt mua hết** (ISSUED): Toàn bộ số lượng đã xuất cho nhà phân phối khác.
- **Đã hủy** (CANCELLED): Giao dịch không thành công.
- **Phát hiện vấn đề sau kiểm định**: Trong trường hợp hiếm gặp, nếu phát hiện vấn đề nghiêm trọng sau khi duyệt QC (ví dụ: kết quả lab bổ sung cho thấy dư lượng vượt ngưỡng), lô sẽ bị thu hồi và biến mất.

## Vòng đời lô hàng từ góc nhìn nhà phân phối
```
Nhà cung cấp giao hàng
    → Kho tiếp nhận và kiểm định QC (nhà phân phối chưa thấy)
    → QC PASS → Lô xuất hiện trong catalog ✅
    → Nhà phân phối đặt mua → Lô được giữ (RESERVED)
    → Kho xử lý và xuất hàng → ISSUED
    → Lô biến khỏi catalog
```

## Thông tin BatchCode là gì?
Mỗi lô hàng có một Mã lô (BatchCode) duy nhất, ví dụ: `PB-20261026-001`, `DEMO-DIST-LOT-01`.
BatchCode dùng để tra cứu thông tin lô, xem báo cáo QC, hoặc quét QR truy xuất nguồn gốc.
Khi hỏi AI về một lô hàng cụ thể, hãy cung cấp BatchCode để nhận thông tin chính xác.
