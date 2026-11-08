using System.Text;

namespace SAW.Infrastructure.AiForecast;

/// <summary>
/// Xây dựng prompt chuẩn chuyên gia phân tích chất lượng chuỗi lạnh nông sản (Cold Chain Quality Analyst)
/// để yêu cầu Gemini dự báo độ hao hụt và đề xuất kế hoạch hành động tối ưu cho Warehouse Manager.
/// </summary>
public static class ForecastPromptBuilder
{
    private const string SystemInstruction = """
        Bạn là Chuyên gia Cấp cao về Quản trị Kinh doanh Nông sản & Bảo quản Sau thu hoạch (Senior Agri-Business Commercial & Post-Harvest Strategist) thuộc Hệ thống Quản trị Kho SAW.
        
        TÔN CHỈ PHÂN TÍCH:
        "BẢO TOÀN GIÁ TRỊ THƯƠNG MẠI TỐI ĐA & BẢO VỆ DÒNG TIỀN DOANH NGHIỆP: Nông sản tươi giảm giá trị theo từng ngày lưu kho. Mỗi quyết định chậm trễ sẽ biến lợi nhuận thành tổn thất. Hãy phân tích sắc bén, định lượng rõ ràng để Quản lý kho biết chính xác tình trạng hàng, thời điểm vàng bán hàng, mức giá/chiết khấu tối ưu để bán đi với giá tốt nhất trước khi bị lỗ thêm hoặc phải thanh lý giá rẻ."

        CẤU TRÚC BẮT BUỘC CỦA BÀI NHẬN ĐỊNH (Trường "recommendation"):
        Bài nhận định phải viết bằng tiếng Việt chuyên nghiệp, súc tích, mạch lạc, tập trung hoàn toàn vào:

        ### ⚠️ Nguyên Nhân Dẫn Đến Độ Hao Hụt Cao & Giải Pháp Khắc Phục
        - Phân tích rõ các nguyên nhân cốt lõi dẫn đến rủi ro hao hụt hiện tại:
          + Dao động chuỗi lạnh: Số lần và biên độ nhiệt ẩm kho bị lệch khỏi ngưỡng bảo quản chuẩn trong 72 giờ qua.
          + Vòng đời nông sản: Số ngày đã qua kể từ ngày thu hoạch và % thời hạn lưu kho an toàn đã tiêu thụ.
          + Trạng thái kiểm định QC: Tỷ lệ khuyết tật dập nát ban đầu, điểm độ chín/độ tươi, hoặc cảnh báo từ phòng lab (vi sinh, hóa chất).
        - Kèm theo giải pháp xử lý trực diện và cấp bách cho từng nguyên nhân cụ thể để ngăn chặn hao hụt và bảo vệ chất lượng lô hàng (ví dụ: hiệu chỉnh nhiệt độ buồng kho về mức chuẩn ngay lập tức, tăng độ ẩm chống mất nước/héo cuống, cô lập vị trí, hoặc lọc bỏ quả dập để triệt tiêu nguồn lây mốc/nấm).

        YÊU CẦU ĐỊNH DẠNG JSON:
        - Trả về đúng cấu trúc JSON thuần túy, không bao bọc thêm văn bản ngoài.
        """;

    public static string BuildSystemPrompt() => SystemInstruction;

    public static string BuildUserPrompt(ForecastInputSnapshot snapshot)
    {
        var b = snapshot.Batch;
        var sb = new StringBuilder();

        sb.AppendLine("=== DỮ LIỆU ĐẦU VÀO ĐỂ DỰ BÁO RỦI RO HAO HỤT LÔ HÀNG ===");
        sb.AppendLine();
        
        // 1. Thông tin lô hàng
        sb.AppendLine("1. THÔNG TIN LÔ HÀNG:");
        sb.AppendLine($"- Mã lô (BatchCode): {b.BatchCode}");
        sb.AppendLine($"- Tên sản phẩm: {b.ProductName}");
        sb.AppendLine($"- Loại nông sản: {b.CropType?.CropName ?? "N/A"} (Nhóm: {b.CropType?.CategoryName ?? "N/A"})");
        sb.AppendLine($"- Nhà cung cấp: {b.Supplier?.SupplierName ?? "N/A"}");
        sb.AppendLine($"- Vùng trồng: {b.GrowingArea?.AreaName ?? "N/A"}, {b.GrowingArea?.District ?? ""}, {b.GrowingArea?.Province ?? ""}");
        sb.AppendLine($"- Trọng lượng nhập/hiện tại: {b.WeightInKg:F1} kg");
        sb.AppendLine($"- Ngày thu hoạch: {b.HarvestDate:yyyy-MM-dd}");
        sb.AppendLine($"- Ngày hết hạn công bố: {(b.ExpiryDate.HasValue ? b.ExpiryDate.Value.ToString("yyyy-MM-dd") : "Chưa có")}");
        sb.AppendLine($"- Số ngày đã qua kể từ thu hoạch: {snapshot.DaysFromHarvest} ngày");
        if (snapshot.DaysUntilExpiry.HasValue)
            sb.AppendLine($"- Số ngày còn lại đến hạn công bố: {snapshot.DaysUntilExpiry.Value} ngày");
        if (snapshot.ShelfLifeConsumedPct.HasValue)
            sb.AppendLine($"- Tỷ lệ vòng đời lưu kho đã tiêu thụ: {snapshot.ShelfLifeConsumedPct.Value:F1}%");
        sb.AppendLine($"- Phân hạng chất lượng hiện tại: {b.QualityGrade ?? "Chưa phân hạng"}");
        sb.AppendLine($"- Trạng thái lô hàng: {b.BatchStatus}");
        sb.AppendLine($"- Đóng gói: {b.PackagingType ?? "Tiêu chuẩn"}");

        // 2. Tiêu chuẩn bảo quản chuẩn của loại nông sản
        sb.AppendLine();
        sb.AppendLine("2. YÊU CẦU BẢO QUẢN TIÊU CHUẨN (Cơ sở so sánh):");
        var minTemp = b.ExpectedMinTempC ?? b.CropType?.ExpectedMinTempC;
        var maxTemp = b.ExpectedMaxTempC ?? b.CropType?.ExpectedMaxTempC;
        var minHum = b.ExpectedMinHumidityPct ?? b.CropType?.ExpectedMinHumidityPct;
        var maxHum = b.ExpectedMaxHumidityPct ?? b.CropType?.ExpectedMaxHumidityPct;
        var shelfDays = b.ShelfLifeDaysSnapshot ?? b.CropType?.ShelfLifeDays;

        sb.AppendLine($"- Dải nhiệt độ bảo quản chuẩn: {(minTemp.HasValue ? $"{minTemp}°C" : "N/A")} đến {(maxTemp.HasValue ? $"{maxTemp}°C" : "N/A")}");
        sb.AppendLine($"- Dải độ ẩm không khí chuẩn: {(minHum.HasValue ? $"{minHum}%" : "N/A")} đến {(maxHum.HasValue ? $"{maxHum}%" : "N/A")}");
        sb.AppendLine($"- Thời hạn lưu kho an toàn tối đa khuyến nghị: {(shelfDays.HasValue ? $"{shelfDays} ngày" : "N/A")}");

        // 3. Kết quả kiểm định chất lượng (QC)
        sb.AppendLine();
        sb.AppendLine("3. KẾT QUẢ KIỂM ĐỊNH CHẤT LƯỢNG (QC):");
        if (snapshot.QcInspection == null)
        {
            sb.AppendLine("⚠️ CHƯA CÓ KẾT QUẢ KIỂM ĐỊNH QC HOÀN TẤT. Cần đánh giá rủi ro dựa trên độ tươi mặc định và khuyến nghị kiểm tra QC ngay.");
        }
        else
        {
            var qc = snapshot.QcInspection;
            sb.AppendLine($"- Mã phiếu kiểm định: {qc.InspectionCode}");
            sb.AppendLine($"- Kết luận QC: {qc.QcResult} (Hạng đánh giá: {qc.QualityGrade})");
            sb.AppendLine($"- Thời điểm kiểm định: {(qc.CompletedAt.HasValue ? qc.CompletedAt.Value.ToString("yyyy-MM-dd HH:mm") : "N/A")}");

            if (qc.SensoryResult != null)
            {
                sb.AppendLine("  + Đánh giá cảm quan:");
                sb.AppendLine($"    * Điểm độ tươi: {qc.SensoryResult.FreshnessScore}/10");
                sb.AppendLine($"    * Điểm màu sắc: {qc.SensoryResult.ColorScore}/10");
                sb.AppendLine($"    * Điểm độ chín: {qc.SensoryResult.RipenessScore}/10");
                sb.AppendLine($"    * Điểm kích thước đồng đều: {qc.SensoryResult.SizeScore}/10");
                sb.AppendLine($"    * Tỷ lệ dập nát/khuyết tật ban đầu: {qc.SensoryResult.DamagePercentage:F1}%");
                if (!string.IsNullOrWhiteSpace(qc.SensoryResult.Note))
                    sb.AppendLine($"    * Ghi chú cảm quan: {qc.SensoryResult.Note}");
            }

            if (qc.LabResult != null)
            {
                sb.AppendLine("  + Kiểm nghiệm phòng Lab:");
                sb.AppendLine($"    * Dư lượng hóa chất BVTV: {qc.LabResult.ChemicalResidueStatus} ({qc.LabResult.ResidueValue} {qc.LabResult.ResidueUnit})");
                sb.AppendLine($"    * Mầm bệnh / Vi sinh: {qc.LabResult.PathogenStatus} {(string.IsNullOrEmpty(qc.LabResult.PathogenName) ? "" : $"({qc.LabResult.PathogenName})")}");
            }

            if (qc.ResultDetails.Count > 0)
            {
                sb.AppendLine("  + Chi tiết các chỉ số kiểm tra:");
                foreach (var d in qc.ResultDetails)
                {
                    var val = d.NumericValue.HasValue ? $"{d.NumericValue.Value:F1} {d.InspectionCriterion?.Unit}" : d.TextValue;
                    sb.AppendLine($"    * {d.InspectionCriterion?.CriterionName}: {val} (Đạt: {(d.IsPassed ? "Có" : "Không")}, Hạng: {d.EvaluatedGrade})");
                }
            }
        }

        // 4. Nhật ký điều kiện môi trường thực tế (72 giờ qua)
        sb.AppendLine();
        sb.AppendLine("4. ĐIỀU KIỆN MÔI TRƯỜNG THỰC TẾ TRONG KHO (72 GIỜ QUA):");
        if (snapshot.EnvStats == null)
        {
            sb.AppendLine("⚠️ CHƯA CÓ NHẬT KÝ CẢM BIẾN MÔI TRƯỜNG CHO LÔ HÀNG NÀY.");
        }
        else
        {
            var st = snapshot.EnvStats;
            sb.AppendLine($"- Tổng số lần đo ghi nhận: {st.LogCount} lần trong 72 giờ qua");
            sb.AppendLine($"- Nhiệt độ trung bình: {st.AvgTempC}°C (Thấp nhất: {st.MinTempC}°C, Cao nhất: {st.MaxTempC}°C)");
            if (st.AvgHumidityPct.HasValue)
                sb.AppendLine($"- Độ ẩm không khí trung bình: {st.AvgHumidityPct.Value}%");
            sb.AppendLine($"- Số lần đo bị LỆCH KHỎI NGƯỠNG BẢO QUẢN CHO PHÉP: {st.TempDeviationCount} lần");

            if (snapshot.RecentEnvLogs.Count > 0)
            {
                sb.AppendLine("  + Các lần đo gần nhất:");
                foreach (var log in snapshot.RecentEnvLogs.Take(5))
                {
                    sb.AppendLine($"    * {log.RecordedAt:yyyy-MM-dd HH:mm}: Nhiệt độ {log.TemperatureC}°C, Độ ẩm {log.HumidityPct?.ToString("F0") ?? "N/A"}% (Nguồn: {log.SourceType})");
                }
            }
        }

        // 5. Vị trí kho hiện tại
        sb.AppendLine();
        sb.AppendLine("5. VỊ TRÍ LƯU TRỮ TRONG KHO:");
        if (snapshot.CurrentLocation == null)
        {
            sb.AppendLine("⚠️ Chưa chỉ định vị trí kho cụ thể hoặc lô hàng đang ở khu vực đệm chờ xếp.");
        }
        else
        {
            var loc = snapshot.CurrentLocation;
            sb.AppendLine($"- Mã vị trí: {loc.LocationCode}");
            sb.AppendLine($"- Khu vực kho: {loc.ZoneName} (Kệ: {loc.RackName ?? "N/A"}, Ô: {loc.BinName ?? "N/A"})");
            if (loc.MinTempC.HasValue || loc.MaxTempC.HasValue)
                sb.AppendLine($"- Thiết kế kỹ thuật khu vực: Nhiệt độ {loc.MinTempC}°C - {loc.MaxTempC}°C, Độ ẩm {loc.MinHumidityPct}% - {loc.MaxHumidityPct}%");
        }

        // 6. Cấu trúc JSON mong đợi
        sb.AppendLine();
        sb.AppendLine("=== CẤU TRÚC JSON PHẢI TRẢ VỀ ===");
        sb.AppendLine("""
            Hãy trả về đúng cấu trúc JSON sau:
            {
              "spoilageRiskScore": <số thực từ 0 đến 100, ví dụ: 45.0>,
              "predictedLossPercent": <số thực từ 0 đến 100, % hao hụt lượng/chất lượng ước tính, ví dụ: 6.5>,
              "safeStorageRemainingDays": <số nguyên ngày lưu kho an toàn còn lại, ví dụ: 5>,
              "riskLevel": "<chính xác một trong các giá trị: LOW | MEDIUM | HIGH | CRITICAL>",
              "forecastHorizonDays": <số ngày trong phạm vi dự báo, ví dụ: 7>,
              "recommendation": "<Bài phân tích chi tiết từ 250 đến 400 từ, định dạng Markdown rõ ràng tập trung vào:
              ### ⚠️ Nguyên Nhân Dẫn Đến Độ Hao Hụt Cao & Giải Pháp Khắc Phục
              (Liệt kê rõ từng nguyên nhân cốt lõi từ nhiệt độ độ ẩm chuỗi lạnh 72h qua, số ngày sau thu hoạch, chỉ số QC ban đầu... kèm theo giải pháp khắc phục trực diện cho từng nguyên nhân cụ thể)>"
            }
            """);

        return sb.ToString();
    }
}
