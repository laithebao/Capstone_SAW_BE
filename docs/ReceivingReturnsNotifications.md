# Hoàn tất đơn, từ chối nhận hàng và notification

## Cập nhật máy test

Pull cả BE và FE. Trong Package Manager Console:

```powershell
Update-Database -Project SAW.Infrastructure -StartupProject SAW.API
```

Migration mới nhất: `20261031140934_AddLotDeliveryRefusalScope` bổ sung phạm vi lô cho từng lần từ chối và trạng thái `PARTIALLY_REFUSED`. Migration trước đó `20261031135256_AddOrderDeliveryRefusal` tạo hồ sơ và ảnh bằng chứng. Giữ nguyên các migration cũ; nếu đã cập nhật bản trước thì vẫn cần chạy Update-Database cho migration mới. Hồ sơ từ chối toàn đơn cũ được gắn với toàn bộ lô vốn thuộc đơn đó, giữ nguyên lý do và ảnh. Không cần seed lại đơn hoặc lô hàng. Restart API và FE sau khi cập nhật.

## Nhận hàng / từ chối nhận đơn

- Khách xác nhận nhận đủ hàng trên chi tiết đơn sau khi đã xuất đủ. Đơn chuyển `DELIVERED`; đây là đơn thành công để tính số tiền đã chi tiêu, không xác nhận thanh toán.
- Thanh Nhận hàng và xử lý đơn hiển thị Đã nhận đủ hàng và Từ chối nhận đơn. Sau khi nhận đủ, nút nhận hàng được thay bằng trạng thái xác nhận; vẫn cho phép từ chối nếu phát hiện hàng có vấn đề.
- Chọn một hoặc nhiều lô bị lỗi khi `ISSUED`, `DISPATCHED`, `DELIVERED` hoặc `PARTIALLY_REFUSED`. Chỉ lô thuộc đơn hiện tại, đã xuất đủ bằng phiếu COMMITTED và chưa bị từ chối mới hợp lệ. Từ chối nguyên lô, không nhập số kg tùy ý; các lô không chọn giữ nguyên.
- Lý do bắt buộc 1–1000 ký tự và từ 1–5 ảnh JPG/PNG/WEBP thật, tối đa 10 MB mỗi ảnh. FE xem trước ảnh; BE kiểm tra kích thước và chữ ký nội dung.
- Mỗi lần gửi lưu hồ sơ, các lô được chọn trong `ORDER_DELIVERY_REFUSAL_LOT` và ảnh. Đơn thành PARTIALLY_REFUSED khi còn lô không bị từ chối; chỉ thành DELIVERY_REFUSED khi tất cả lô đã bị từ chối. Có thể gửi lần khác cho lô còn lại, mỗi lần có lý do/ảnh riêng. Một lô chỉ được từ chối một lần. Gửi lặp cùng request ID, phạm vi lô, lý do và ảnh không tạo trùng. Thông báo chỉ nêu các lô bị từ chối.
- Kết thúc luồng xử lý trên hệ thống tại đây. Không duyệt trả hàng, không vận chuyển hàng về kho, không tạo phiếu nhập, không cộng tồn, không mở bán lại và không đảo phiếu xuất. Giải quyết hàng, tiền và đối soát thực hiện bên ngoài.
- Bảng lô hiển thị riêng Đã từ chối nhận / Đã nhận / Chưa xác nhận. Nếu chưa nhận hàng, vẫn xác nhận nhận các lô còn lại được và giữ trạng thái PARTIALLY_REFUSED. Nếu đã xác nhận trước đó, giữ thời điểm cũ; các lô không bị từ chối vẫn đã nhận. Dashboard tính đơn đã xác nhận nhận và còn ít nhất một lô được nhận; giá trị đã nhận chỉ gồm các lô không bị từ chối. Khi từ chối hết các lô, đơn không còn được tính vào số đơn nhận thành công/giá trị đã nhận. Không đổi tổng đơn gốc hoặc tự ghi nhận hoàn tiền.
- Ảnh lưu riêng trong `App_Data/DeliveryRefusalFiles` (đổi qua `DeliveryRefusalFiles:StoragePath` khi cần). Không đặt trong wwwroot; chỉ chủ đơn và quản lý kho được đọc qua API xác thực. Triển khai nhiều instance phải cấu hình nơi lưu dùng chung và backup ảnh cùng dữ liệu.
- API kiểm tra ownership và role; thao tác khóa đơn, ghi người/thời điểm, retry không tạo trùng.

## Notification

Thông báo được lưu cùng giao dịch thay đổi trạng thái. Rollback nghiệp vụ cũng rollback thông báo. Không gửi email cho mọi sự kiện; notification này nằm ở chuông trong hệ thống.

| Sự kiện | Người nhận |
|---|---|
| Lô SUBMITTED, chờ kiểm nhận | Staff, supplier sở hữu lô |
| Lô PENDING_QC | QC, supplier sở hữu lô |
| Lô đạt QC/chờ nhập kho, tạm giữ | Staff, supplier sở hữu lô |
| Lô đã nhập kho | Manager kiểm tra giá bán, supplier |
| Tạo hoặc sửa phiếu nhập/xuất kho nháp | Không gửi thông báo; chỉ gửi khi xác nhận nhập/xuất kho |
| Xác nhận phiếu nhập/xuất kho | Người lập, người xác nhận và manager; supplier/khách vẫn nhận cập nhật trạng thái lô/đơn tương ứng |
| Giá lô bị dừng bán do điều chỉnh | Manager kiểm tra/định giá lại |
| Lưu thay đổi giá bán nguyên lô | Manager; thông báo có mã lô, giá mới và trạng thái công bố bán |
| Đặt/hủy/duyệt/từ chối/giữ/soạn/xuất/nhận đơn | Khách sở hữu đơn, manager; staff khi cần giữ/soạn/xuất |
| Phiếu hao hụt/hư hỏng/loại bỏ chờ xử lý | Manager, staff, khách có đơn liên quan |
| Quản lý xử lý phiếu / gửi đề xuất / khách phản hồi | Staff lập phiếu và staff vận hành, manager; khách sở hữu đơn liên quan |
| Tạo, sửa, xác nhận, duyệt/từ chối/hủy hợp đồng | Supplier của hợp đồng, manager phụ trách, admin |
| Khách từ chối lô đã chọn, kèm lý do và ảnh | Khách sở hữu đơn, manager |

Các tài khoản nhận phải ACTIVE. Các vai trò vận hành nhận theo nhóm, supplier/distributor chỉ nhận hồ sơ của mình. Khi staff xác nhận hoặc từ chối kiểm nhận bằng cập nhật SQL có điều kiện, thông báo được lưu trong cùng giao dịch dù EF không theo dõi thay đổi trạng thái đó. Xác nhận lại không tạo thêm thông báo.

Chuông kiểm tra mỗi 5 giây khi tab đang hiển thị, kiểm tra ngay khi quay lại tab và sau thao tác ghi thành công ở tab hiện tại. Số đỏ là tổng thông báo chưa đọc, không giới hạn ở 20 dòng gần nhất. Chỉ mở chuông mới đánh dấu đã đọc và xóa số đỏ; tải danh sách hoặc thao tác nghiệp vụ không đánh dấu đã đọc. Thông báo đến sau lần mở chuông vẫn được tính là mới. Có thể xem thông báo cũ hơn và bấm thông báo để điều hướng tới màn liên quan.

Thông báo phiếu dùng ID phiếu đã lưu và cùng giao dịch nghiệp vụ; lỗi lưu hoặc rollback không để lại thông báo thành công. Retry hoặc xác nhận lại không tạo thêm thông báo. Staff mở được phiếu tương ứng từ chuông; manager mở phiếu nhập hoặc màn đơn hàng khi thông báo liên quan phiếu xuất, theo quyền truy cập hiện có.

Thông báo tạo phiếu nháp đã lưu trước đây được giữ trong dữ liệu lịch sử nhưng không trả về danh sách chuông, không tính vào số chưa đọc. Lưu cùng mức giá hiện tại không tạo thêm thông báo cập nhật giá; lỗi kiểm tra hoặc rollback cũng không để lại thông báo giá đã lưu.

## Đăng ký/đăng nhập Google

Tài khoản mới được gửi email xác nhận và chưa được cấp phiên đăng nhập cho đến khi xác nhận. Đăng nhập tài khoản PENDING hiển thị hướng dẫn kiểm tra email/thư rác. FE giữ lỗi xác thực trên form; không chuyển lỗi đó sang trang 403 báo cần Admin cấp quyền. Tài khoản bị khóa/vô hiệu hóa vẫn không được đăng nhập. SMTP cần được cấu hình đúng như luồng đăng ký hiện có.

## Kiểm thử thủ công

1. Dùng hai distributor: đặt lô, kiểm tra manager nhận thông báo, người còn lại không nhận dữ liệu đơn.
2. Hủy một đơn PENDING bằng popup, kiểm tra manager nhận thông báo và lô được mở lại khi đủ điều kiện.
3. Đơn khác: manager duyệt/giữ → staff soạn/xuất đủ → khách nhận thông báo → xác nhận nhận đủ → kiểm tra DELIVERED và dashboard.
4. Đơn nhiều lô đã xuất đủ hoặc đã xác nhận nhận hàng: bấm Từ chối lô hàng → chọn lô lỗi → nhập lý do, chọn ảnh → xác nhận → kiểm tra PARTIALLY_REFUSED, lô còn lại không bị từ chối và vẫn nhận được. Chọn tiếp lô còn lại ở lần khác → nếu hết lô thì DELIVERY_REFUSED. Ảnh vẫn tải được sau reload, quản lý nhận thông báo đúng lô, tồn kho không tăng và không tạo phiếu nhập.
5. Thử gửi đơn người khác, gửi lặp, thiếu lý do/ảnh, ảnh giả/ảnh quá lớn, đơn chưa xuất đủ; không tạo hồ sơ hoặc thông báo trùng. Chủ đơn khác không tải được ảnh.
6. Supplier gửi lô → staff kiểm nhận → QC kiểm định → staff nhập kho; kiểm tra chuông ở từng vai trò và supplier.
7. Tạo phiếu hao hụt/loại bỏ ở lô đang giao dịch, manager xử lý và gửi đề xuất; kiểm tra staff, manager, đúng khách nhận thông báo.
8. Manager tạo hợp đồng → supplier xác nhận → admin duyệt; kiểm tra chuông các bên.
9. Google tài khoản mới: chọn role, kiểm tra hướng dẫn xác nhận email; xác nhận xong mới đăng nhập. Thử sai mật khẩu và tài khoản bị khóa để bảo đảm lỗi không bị đổi thành lỗi email.

Các test SQL tạo database tên GUID riêng, không thay đổi database làm việc. Đặt `SAW_TEST_SQL_CONNECTION` trước khi chạy `dotnet test` nếu muốn chạy nhóm test SQL.

## API từ chối nhận đơn

- `POST /api/order-delivery-refusals/{orderId}`: multipart gồm `requestId` (GUID), `reason`, các phần `batchIds` (mã số lô được chọn) và 1–5 phần `images`. Chỉ distributor sở hữu đơn được gửi; kết quả là hồ sơ của lần gửi đó.
- `GET /api/order-delivery-refusals/{orderId}`: danh sách các lần từ chối với phạm vi lô, lý do, thời gian và ảnh. Chủ đơn và quản lý kho được xem.
- `GET /api/order-delivery-refusals/{orderId}/evidence/{imageId}`: tải ảnh bằng Bearer token; FE dùng blob URL để xem trước/tải về, không đưa token vào URL.
- `POST /api/order-returns/{orderId}` đã dừng (410). GET lịch sử cũ và bảng `ORDER_RETURN` được giữ, không chuyển hồ sơ cũ thành từ chối hoặc tạo ảnh bằng chứng giả.
