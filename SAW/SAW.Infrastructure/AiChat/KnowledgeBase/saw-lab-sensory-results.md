# Đọc Hiểu Báo Cáo Kiểm Định Chất Lượng — Dành cho Nhà Phân Phối

## Báo cáo QC gồm những gì?
Khi xem thông tin một lô hàng, nhà phân phối có thể xem báo cáo kiểm định bao gồm 2 phần: kết quả cảm quan và kết quả phòng lab.

## Kết quả cảm quan — QC Staff đánh giá tại kho

| Chỉ số | Ý nghĩa thực tế |
|---|---|
| Độ tươi (FreshnessScore) | Hàng tươi đến mức nào khi nhập kho |
| Độ đồng đều kích cỡ (SizeScore) | Hàng có đều kích cỡ không — quan trọng với bán lẻ |
| Màu sắc (ColorScore) | Màu sắc đặc trưng của loại nông sản |
| Độ chín (RipenessScore) | Mức độ chín — ảnh hưởng đến thời gian sử dụng còn lại |
| Tỷ lệ hư hỏng (DamagePercentage) | Phần trăm hàng bị dập, nám, hư — **con số này càng thấp càng tốt** |

**Tỷ lệ hư hỏng cao** (DamagePercentage lớn) là dấu hiệu cần chú ý — đặc biệt nếu lô chỉ đạt hạng C, D, E do chỉ tiêu này.

## Kết quả phòng lab — An toàn thực phẩm

| Kết quả | Ý nghĩa cho nhà phân phối |
|---|---|
| **Dư lượng hóa chất: PASS** | Thuốc BVTV trong ngưỡng an toàn — hàng đủ điều kiện phân phối |
| **Dư lượng hóa chất: FAIL** | Vượt ngưỡng — lô bị loại, nhà phân phối sẽ không thấy lô này |
| **Vi sinh: PASS** | Không phát hiện vi sinh nguy hiểm |
| **Vi sinh: FAIL** | Phát hiện vi sinh (ghi rõ tên) — lô bị loại ngay |
| **NOT_TESTED** | Chỉ tiêu này không áp dụng cho loại nông sản này |

## Tiêu chí nào kéo hạng xuống?
Hệ thống tự động xác định "tiêu chí quyết định hạng" — tiêu chí có điểm thấp nhất.
Khi AI giải thích kết quả QC một lô, có thể hỏi: *"Tiêu chí nào khiến lô này chỉ đạt hạng C?"* để biết điểm yếu cụ thể và quyết định có nên mua không.

## Câu hỏi thực tế nhà phân phối hay hỏi
- "Lô [BatchCode] đạt hạng mấy và vì sao?"
- "Tỷ lệ hư hỏng của lô này là bao nhiêu?"
- "Kết quả kiểm tra dư lượng hóa chất của lô này như thế nào?"
- "Lô này còn hạn sử dụng bao lâu?"
- "Điều kiện bảo quản tốt nhất cho lô [loại nông sản] này là gì?"
