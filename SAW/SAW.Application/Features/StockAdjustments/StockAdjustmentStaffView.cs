namespace SAW.Application.Features.StockAdjustments;

// Operation endpoints expose warehouse progress, not customer negotiations.
public static class StockAdjustmentStaffView
{
    public static string? State(string? state) => state is "PENDING_CUSTOMER" or "CUSTOMER_ACCEPTED" or "CUSTOMER_DECLINED" or "ORDER_CANCELLED"
        ? "PENDING_MANAGER" : state;

    public static AdjustmentSummary Summary(AdjustmentSummary summary) => summary with { WorkState = State(summary.WorkState) };

    public static AdjustmentDetail Detail(AdjustmentDetail detail) => detail with {
        Summary = Summary(detail.Summary),
        Resolution = detail.Resolution is { } resolution ? resolution with {
            State = State(resolution.State)!, Order = null, Proposals = [], UpdatedBy = null
        } : null
    };

    public static AdjustmentPage<AdjustmentSummary> Page(AdjustmentPage<AdjustmentSummary> page) => page with {
        Items = page.Items.Select(Summary).ToList()
    };
}
