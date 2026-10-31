# Hoàn tất đơn, trả hàng và notification

## Cập nhật máy test

Pull cả BE và FE. Trong Package Manager Console:

```powershell
Update-Database -Project SAW.Infrastructure -StartupProject SAW.API
```

Migration mới: `20261029150053_AddOrderReturnWorkflow` tạo `ORDER_RETURN`, giữ lịch sử đơn hiện có và bổ sung trạng thái `RETURNED`. Không cần seed lại đơn hoặc lô hàng. Restart API và FE sau khi cập nhật.

## Nhận hàng / trả hàng

- Khách xác nhận nhận đủ hàng trên chi tiết đơn sau khi đã xuất đủ. Đơn chuyển `DELIVERED`; đây là đơn thành công để tính số tiền đã chi tiêu, không xác nhận thanh toán.
- Thanh Nhận hàng và xử lý đơn hiển thị hai lựa chọn: Đã nhận đủ hàng và Yêu cầu trả hàng. Khi mới giữ/soạn hàng, nút bị khóa kèm giải thích; phải xuất đủ hàng trước khi xác nhận nhận hàng.
- Khách có thể yêu cầu trả nguyên đơn khi `ISSUED`, `DISPATCHED` hoặc `DELIVERED`; phải có phiếu xuất đã COMMITTED. Mỗi đơn có một hồ sơ trả hàng, lý do bắt buộc. Không hỗ trợ trả lẻ kg hoặc một phần đơn.
- Phạm vi trả hàng hiện tại kết thúc ở distributor: bấm Yêu cầu trả hàng, nhập lý do, xác nhận; hệ thống lưu hồ sơ REQUESTED và thông báo cho quản lý. Không chuyển đơn sang RETURNED và không thay đổi tồn kho khi gửi yêu cầu.
- Chưa triển khai giao diện/API duyệt, từ chối, kiểm nhận hàng trả hoặc tái nhập. Các phần này do thành viên phụ trách quản lý/staff thực hiện tiếp. Schema và các trường phản hồi được giữ để tích hợp, distributor chỉ đọc hồ sơ của mình.
- Khi yêu cầu đang REQUESTED/APPROVED, khách không thể xác nhận đã nhận đủ hàng. Nếu quản lý từ chối trả hàng, khách vẫn có thể xác nhận nhận hàng khi đủ điều kiện.
- Gửi yêu cầu không tự cộng tồn bán được, không mở lại giá bán và không đảo phiếu xuất đã COMMITTED. Tiền/hoàn tiền xử lý ngoài hệ thống.
- API kiểm tra ownership và role; thao tác khóa đơn, ghi người/thời điểm, retry không tạo trùng.

## Notification

Thông báo được lưu cùng giao dịch thay đổi trạng thái. Rollback nghiệp vụ cũng rollback thông báo. Không gửi email cho mọi sự kiện; notification này nằm ở chuông trong hệ thống.

| Sự kiện | Người nhận |
|---|---|
| Lô SUBMITTED, chờ kiểm nhận | Staff, supplier sở hữu lô |
| Lô PENDING_QC | QC, supplier sở hữu lô |
| Lô đạt QC/chờ nhập kho, tạm giữ | Staff, supplier sở hữu lô |
| Lô đã nhập kho | Manager kiểm tra giá bán, supplier |
| Giá lô bị dừng bán do điều chỉnh | Manager kiểm tra/định giá lại |
| Đặt/hủy/duyệt/từ chối/giữ/soạn/xuất/nhận đơn | Khách sở hữu đơn, manager; staff khi cần giữ/soạn/xuất |
| Phiếu hao hụt/hư hỏng/loại bỏ chờ xử lý | Manager, staff, khách có đơn liên quan |
| Quản lý xử lý phiếu / gửi đề xuất / khách phản hồi | Staff lập phiếu và staff vận hành, manager; khách sở hữu đơn liên quan |
| Tạo, sửa, xác nhận, duyệt/từ chối/hủy hợp đồng | Supplier của hợp đồng, manager phụ trách, admin |
| Khách gửi yêu cầu trả hàng | Khách sở hữu đơn, manager |

Các tài khoản nhận phải ACTIVE. Các vai trò vận hành nhận theo nhóm, supplier/distributor chỉ nhận hồ sơ của mình. Chuông cập nhật mỗi 15 giây, bấm thông báo điều hướng tới màn liên quan; có thể xem thông báo cũ hơn, đánh dấu đã đọc.

## Đăng ký/đăng nhập Google

Tài khoản mới được gửi email xác nhận và chưa được cấp phiên đăng nhập cho đến khi xác nhận. Đăng nhập tài khoản PENDING hiển thị hướng dẫn kiểm tra email/thư rác. FE giữ lỗi xác thực trên form; không chuyển lỗi đó sang trang 403 báo cần Admin cấp quyền. Tài khoản bị khóa/vô hiệu hóa vẫn không được đăng nhập. SMTP cần được cấu hình đúng như luồng đăng ký hiện có.

## Kiểm thử thủ công

1. Dùng hai distributor: đặt lô, kiểm tra manager nhận thông báo, người còn lại không nhận dữ liệu đơn.
2. Hủy một đơn PENDING bằng popup, kiểm tra manager nhận thông báo và lô được mở lại khi đủ điều kiện.
3. Đơn khác: manager duyệt/giữ → staff soạn/xuất đủ → khách nhận thông báo → xác nhận nhận đủ → kiểm tra DELIVERED và dashboard.
4. Đơn đã xuất khác: khách bấm Yêu cầu trả hàng, nhập lý do và xác nhận → kiểm tra REQUESTED, thông báo quản lý, trạng thái đơn và tồn kho giữ nguyên. Tải lại trang vẫn thấy yêu cầu đã gửi.
5. Thử gửi yêu cầu của đơn người khác, gửi lặp cùng lý do, gửi lại lý do khác và gửi khi đơn chưa xuất kho; không tạo hồ sơ hoặc thông báo trùng.
6. Supplier gửi lô → staff kiểm nhận → QC kiểm định → staff nhập kho; kiểm tra chuông ở từng vai trò và supplier.
7. Tạo phiếu hao hụt/loại bỏ ở lô đang giao dịch, manager xử lý và gửi đề xuất; kiểm tra staff, manager, đúng khách nhận thông báo.
8. Manager tạo hợp đồng → supplier xác nhận → admin duyệt; kiểm tra chuông các bên.
9. Google tài khoản mới: chọn role, kiểm tra hướng dẫn xác nhận email; xác nhận xong mới đăng nhập. Thử sai mật khẩu và tài khoản bị khóa để bảo đảm lỗi không bị đổi thành lỗi email.

Các test SQL tạo database tên GUID riêng, không thay đổi database làm việc. Đặt `SAW_TEST_SQL_CONNECTION` trước khi chạy `dotnet test` nếu muốn chạy nhóm test SQL.

## Điểm bàn giao phần trả hàng

- `POST /api/order-returns/{orderId}`: distributor gửi `{ "note": "Lý do trả hàng" }`. Mỗi đơn một hồ sơ, gửi lại cùng nội dung không tạo trùng.
- `GET /api/order-returns/{orderId}` và `GET /api/order-returns`: distributor chỉ xem yêu cầu thuộc đơn của mình.
- Bảng `ORDER_RETURN` giữ mã đơn, lý do, người gửi, thời điểm và trạng thái REQUESTED. Các trường review/receipt đã có trong schema, dành cho phần xử lý tiếp.
- Khi triển khai tiếp, quản lý/staff cần API theo role riêng, kiểm tra trạng thái và thực tế nhận hàng. Không đánh dấu đơn RETURNED chỉ vì khách đã gửi yêu cầu.
