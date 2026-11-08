namespace SAW.Application.Features.SpoilageForecast;

public interface ISpoilageForecastService
{
    /// <summary>
    /// Tạo hoặc lấy dự báo hao hụt nông sản mới nhất cho một lô hàng.
    /// </summary>
    Task<SpoilageForecastResponse> GenerateForecastAsync(
        int requestedByAccountId,
        SpoilageForecastRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Lấy dự báo gần nhất đã lưu trong cơ sở dữ liệu (không gọi lại Gemini).
    /// </summary>
    Task<SpoilageForecastResponse?> GetLatestForecastAsync(
        long productBatchId,
        CancellationToken ct = default);

    /// <summary>
    /// Lấy lịch sử các lần dự báo hao hụt của lô hàng.
    /// </summary>
    Task<IReadOnlyList<SpoilageForecastResponse>> GetForecastHistoryAsync(
        long productBatchId,
        int count = 5,
        CancellationToken ct = default);

    /// <summary>
    /// Lấy danh sách các lô hàng đang lưu kho có nguy cơ rủi ro cao nhất (HIGH hoặc CRITICAL)
    /// để Warehouse Manager ưu tiên can thiệp.
    /// </summary>
    Task<IReadOnlyList<SpoilageForecastResponse>> GetHighRiskBatchesAsync(
        int limit = 10,
        CancellationToken ct = default);
}
