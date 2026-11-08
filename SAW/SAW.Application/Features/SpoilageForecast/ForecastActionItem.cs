namespace SAW.Application.Features.SpoilageForecast;

public sealed record ForecastActionItem(
    string Priority,     // IMMEDIATE | URGENT | RECOMMENDED | MONITOR
    string Category,     // TEMPERATURE | HUMIDITY | ROTATION | DISPOSAL | INSPECTION | TRANSFER | OTHER
    string Action,       // Hành động cụ thể
    string Reason,       // Căn cứ khoa học / lý do
    string? Deadline     // Thời hạn khuyến nghị (ví dụ: "Trong 6 giờ", "Trước ngày mai")
);
