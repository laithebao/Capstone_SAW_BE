# Đặt Hàng và Theo Dõi Đơn Hàng — Dành cho Nhà Phân Phối SAW

## Tôi có thể mua lô hàng bằng cách nào? (Quy trình mua hàng từng bước)
Nhà phân phối đặt mua lô hàng nông sản trên hệ thống SAW qua các bước cụ thể:
1. **Xem catalog lô hàng:** Vào trang Catalog để xem danh sách các lô nông sản đang mở bán, lọc theo loại nông sản, hạng chất lượng (A/B/C/D/E), trọng lượng và giá.
2. **Chọn lô hàng muốn mua:** Nhấp chọn các lô nông sản phù hợp. Mỗi lô được bán theo nguyên lô (hiển thị rõ mã lô BatchCode, trọng lượng kg và giá nguyên lô WholeLotPrice).
3. **Nhập thông tin nhận hàng tại màn hình Tạo đơn hàng:**
   - Kiểm tra lại các lô đã chọn và tổng giá trị đơn hàng.
   - Nhập **Địa chỉ nhận hàng** (bắt buộc).
   - Nhập **Số điện thoại liên hệ** (bắt buộc, từ 10–30 chữ số).
   - Chọn **Ngày mong muốn nhận hàng** (tùy chọn đề xuất cho kho) và ghi chú (nếu có).
4. **Gửi đơn hàng:** Bấm nút **"Gửi đơn chờ duyệt"**. Đơn hàng được tạo thành công với trạng thái `PENDING` (chờ kho duyệt).
5. **Kho SAW xử lý và giao hàng:** Quản lý kho duyệt đơn (`APPROVED`) → nhân viên kho lấy hàng (`PICKED`) → đóng gói (`PREPARED`) → xuất kho (`ISSUED`) → bàn giao vận chuyển (`DISPATCHED`) → giao đến địa chỉ nhà phân phối (`DELIVERED`).

## Lưu ý về đặt mua và thanh toán
- Lô hàng được bán nguyên lô (WholeLotPrice), thanh toán thực hiện theo thỏa thuận thương mại bên ngoài hệ thống.
- Đơn hàng mới gửi (`PENDING`) đang chờ duyệt, chưa giữ hàng độc quyền cho đến khi quản lý kho xác nhận.

## Theo dõi trạng thái đơn hàng

| Trạng thái | Ý nghĩa |
|---|---|
| PENDING | Đơn đang chờ quản lý kho xác nhận duyệt |
| APPROVED | Kho đã duyệt đơn, đang chuẩn bị hàng |
| PICKED | Đã lấy hàng từ các vị trí trong kho |
| PREPARED | Đã đóng gói hoàn tất, sẵn sàng chờ xuất |
| ISSUED / PARTIALLY_ISSUED | Đã xuất kho (toàn bộ hoặc một phần) |
| DISPATCHED | Đã bàn giao cho đơn vị vận chuyển giao hàng |
| DELIVERED | Hàng đã giao thành công đến địa chỉ nhận |
| REJECTED | Đơn bị từ chối bởi kho (ví dụ: hết hàng hoặc không đủ điều kiện) |
| CANCELLED | Đơn đã hủy bởi nhà phân phối hoặc quản lý |

## Giá lô hàng được tính thế nào?
Mỗi lô có giá cả lô (WholeLotPrice) dựa trên trọng lượng thực tế (kg) và đơn giá theo chất lượng.
Hạng chất lượng ảnh hưởng trực tiếp đến giá — lô Hạng A (chất lượng xuất sắc) thường có giá cao hơn lô Hạng B hoặc C cùng loại nông sản.

## Truy xuất nguồn gốc bằng QR Code
Mỗi sản phẩm hoặc pallet trong lô có QR Code riêng. Quét QR để xem:
- Lô hàng xuất phát từ vùng trồng nào, nhà cung cấp nào.
- Ngày thu hoạch, ngày nhập kho.
- Kết quả kiểm định QC đầy đủ (hạng chất lượng, cảm quan, lab).
- Lịch sử di chuyển của lô.

QR Code là bằng chứng minh bạch về nguồn gốc và chất lượng — nhà phân phối có thể cung cấp cho khách hàng cuối để tăng độ tin cậy.

## Tôi có thể hỏi AI điều gì về đơn hàng?
- Hướng dẫn các bước đặt mua và theo dõi đơn hàng trên hệ thống SAW.
- Thông tin chi tiết lô hàng theo Mã lô (BatchCode).
- Kết quả kiểm định QC: hạng chất lượng, cảm quan, kết quả lab.
- Tư vấn điều kiện bảo quản và vận chuyển phù hợp cho từng loại nông sản.
- Giải thích ý nghĩa các chỉ số trong báo cáo QC.
