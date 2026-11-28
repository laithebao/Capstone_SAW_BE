using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SAW.Domain.Entities;
namespace SAW.Infrastructure.Contracts;

public static class ContractDocument
{
    public static SupplyContractRevision Build(SupplyContract c, Supplier? s, CropType? crop, IConfiguration? config)
    {
        var buyer = new { Name = "SMART AGRI-WAREHOUSE (SAW)", TaxCode = config?["SupplyContracts:Buyer:TaxCode"], Address = config?["SupplyContracts:Buyer:Address"], Representative = config?["SupplyContracts:Buyer:Representative"], Contact = config?["SupplyContracts:Buyer:Contact"] };
        var vi = CultureInfo.GetCultureInfo("vi-VN");
        var sentAt = DateTime.UtcNow;
        string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "Chưa cung cấp" : value;
        var content = $"""
            CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM
            Độc lập - Tự do - Hạnh phúc

            HỢP ĐỒNG THU MUA NÔNG SẢN
            Số: {c.ContractCode} · Phiên bản: {c.Revision}
            Ngày gửi: {sentAt.AddHours(7):dd/MM/yyyy HH:mm:ss} (giờ Việt Nam)
            Căn cứ Bộ luật Dân sự số 91/2015/QH13, Luật Thương mại số 36/2005/QH11 và Luật Giao dịch điện tử số 20/2023/QH15 cùng các sửa đổi, bổ sung áp dụng.

            BÊN A – BÊN THU MUA: {buyer.Name}
            Mã số thuế: {Text(buyer.TaxCode)}
            Địa chỉ: {Text(buyer.Address)}
            Người đại diện: {Text(buyer.Representative)}
            Liên hệ: {Text(buyer.Contact)}

            BÊN B – BÊN CUNG CẤP: {Text(s?.SupplierName)}
            Mã số thuế: {Text(s?.TaxCode)}
            Địa chỉ: {Text(s?.Address)}
            Người đại diện pháp lý: {Text(s?.LegalRepresentative)}
            Người liên hệ: {Text(s?.ContactPerson)}
            Điện thoại: {Text(s?.PhoneNumber)} · Email: {Text(s?.Email)}

            Điều 1. Đối tượng thu mua
            Nông sản: {Text(crop?.CropName)}. Mã nông sản: {Text(crop?.CropCode)}.
            Sản lượng cam kết: {c.DeliveryQuantityKg.ToString("N2", vi)} kg mỗi {(c.DeliveryPeriod == "MONTH" ? "tháng" : "tuần")}, theo kỳ giao được hệ thống xác định từ ngày bắt đầu hợp đồng.

            Điều 2. Đơn giá và thanh toán
            Đơn giá: {c.UnitPriceVndPerKg.ToString("N2", vi)} VNĐ/kg.
            Phương thức, thời hạn thanh toán và các chi phí liên quan do hai bên thỏa thuận tại Điều 6 hoặc văn bản bổ sung. Hệ thống không thực hiện thanh toán trực tuyến.

            Điều 3. Thời hạn và giao hàng
            Từ {c.StartDate:dd/MM/yyyy} đến {c.EndDate:dd/MM/yyyy}. Giao theo {(c.DeliveryPeriod == "MONTH" ? "tháng" : "tuần")}; tổng lượng các lô trong kỳ tuân thủ hạn mức đã cam kết.
            Thời điểm và địa điểm giao cụ thể do hai bên thống nhất; không suy diễn tổng giá trị hợp đồng từ số kỳ.

            Điều 4. Kiểm nhận và chất lượng
            Bên B khai báo lô theo hợp đồng. Nhân viên vận hành kiểm nhận thực tế; lô hợp lệ chuyển sang kiểm định QC theo bộ tiêu chuẩn áp dụng. Lô đạt QC mới đủ điều kiện lập và xác nhận phiếu nhập kho.
            Lô có thể bị từ chối trong kiểm nhận hoặc do kết quả QC; lý do được ghi nhận trên hệ thống. Các bên phối hợp xử lý lô bị từ chối theo thỏa thuận, không tự động coi lô khai báo là hàng đã được nhập kho.

            Điều 5. Quyền và nghĩa vụ
            Bên B cung cấp thông tin lô, nguồn gốc và chứng từ đúng thực tế, phối hợp giao hàng theo chu kỳ. Bên A tổ chức kiểm nhận, QC và ghi nhận nhập kho đúng kết quả thực tế. Các bên thông báo sai lệch và phối hợp xử lý theo thỏa thuận và quy định áp dụng.

            Điều 6. Điều khoản bổ sung
            {Text(c.Terms)}

            Điều 7. Thay đổi và chấm dứt
            Nội dung sửa đổi được gửi thành revision mới và thực hiện lại quy trình Supplier xác nhận, Administrator phê duyệt theo hệ thống. Yêu cầu hủy được xử lý theo luồng duyệt hiện hành; không xóa nghĩa vụ, lịch sử hay lô hàng đã phát sinh.
            Trường hợp chậm giao, các bên trao đổi và xử lý theo thỏa thuận; văn bản này không tự đặt mức phạt hoặc tự hủy hợp đồng.

            Điều 8. Tranh chấp và xác nhận
            Các bên ưu tiên thương lượng; nếu không giải quyết được, thực hiện theo thỏa thuận hợp pháp và pháp luật áp dụng. Thời hạn giao hàng không thay thế điều kiện có hiệu lực của giao dịch.
            Hệ thống ghi nhận việc gửi, Supplier xác nhận và Administrator phê duyệt theo đúng revision. Đây là xác nhận trong ứng dụng, không phải chữ ký số được chứng thực.

            """;
        return new SupplyContractRevision { Contract = c, SupplyContractId = c.SupplyContractId, Revision = c.Revision,
            Content = content, SnapshotJson = JsonSerializer.Serialize(new { Buyer = buyer, Supplier = new { s?.SupplierName, s?.TaxCode, s?.Address, s?.LegalRepresentative, s?.ContactPerson, s?.PhoneNumber, s?.Email }, Crop = new { crop?.CropTypeId, crop?.CropCode, crop?.CropName }, c.DeliveryQuantityKg, c.DeliveryPeriod, c.UnitPriceVndPerKg, c.StartDate, c.EndDate, c.Terms }),
            ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), SentAt = sentAt };
    }
}
