using SAW.Application.Features.Traceability.Dtos;

namespace SAW.Application.Features.Traceability.Services;

// Read-only presentation of persisted QC evidence. Never assigns a new grade or evaluates a batch.
internal static class PublicQcPresentation
{
    private const string Grades = "ABCDE";

    public static PublicTraceabilityQuality Create(TraceabilityQcEvidence qc)
    {
        var criteria = qc.Criteria.Select(Map).ToList();
        var graded = criteria.Where(c => c.HasResult && c.DataType is "NUMBER" or "TEXT" && c.EvaluatedGrade != null).ToList();
        var incomplete = criteria.Count == 0 || qc.Criteria.Where((c, index) => c.IsRequired && !criteria[index].HasResult).Any()
            || graded.Any(c => c.EvaluatedGrade!.Length != 1 || !Grades.Contains(c.EvaluatedGrade, StringComparison.Ordinal));
        string explanation;
        IReadOnlyList<string> determining = [];
        if (incomplete)
            explanation = "Giữ kết luận kiểm định đã lưu. Hồ sơ hiện chưa đủ kết quả chi tiết để giải thích hạng của lô.";
        else if (graded.Count == 0 && qc.Grade == "A")
            explanation = "Hồ sơ không ghi nhận tiêu chí có hạng chữ. Quy tắc kiểm định hiện tại dùng hạng A mặc định trong trường hợp này; không có nghĩa mọi tiêu chí đều được đánh giá hạng A. Tiêu chí đạt/không đạt không tham gia xếp hạng chữ.";
        else if (graded.Count > 0 && graded.Max(c => Grades.IndexOf(c.EvaluatedGrade!, StringComparison.Ordinal)) == Grades.IndexOf(qc.Grade!, StringComparison.Ordinal))
        {
            determining = graded.Where(c => c.EvaluatedGrade == qc.Grade).Select(c => c.Name).ToList();
            explanation = $"Hạng chung là hạng thấp nhất trong các tiêu chí có hạng đã lưu. Các tiêu chí được liệt kê dưới đây ghi nhận hạng {qc.Grade}, quyết định hạng chung của lô. Tiêu chí đạt/không đạt được xét riêng, không tham gia xếp hạng chữ.";
        }
        else
            explanation = "Giữ kết luận kiểm định đã lưu. Hạng tổng thể chưa khớp đủ với các kết quả chi tiết hiện có để chỉ ra tiêu chí quyết định.";

        var sampling = qc.SamplingRatio is > 0 and <= 1 && qc.SampleSize is > 0
            ? new PublicTraceabilitySampling(qc.SamplingRatio.Value * 100, qc.SampleSize.Value) : null;
        return new(qc.Grade!, qc.Result!, TraceabilityService.Utc(qc.StartedAt), TraceabilityService.Utc(qc.CompletedAt!.Value),
            qc.Standard, sampling, criteria, explanation, determining);
    }

    private static PublicTraceabilityCriterion Map(TraceabilityCriterionEvidence c)
    {
        var kind = c.DataType.ToUpperInvariant();
        var result = c.Result;
        var hasValue = kind switch
        {
            "NUMBER" => result?.NumericValue != null,
            "TEXT" => !string.IsNullOrWhiteSpace(result?.TextValue),
            "BOOLEAN" => result?.BooleanValue != null,
            _ => false
        };
        // Null optional values are not interpreted using the placeholder IsPassed flag.
        var rules = kind == "BOOLEAN" ? [] : c.Rules.Select(r => new PublicTraceabilityGradeRule(r.Grade,
            kind == "NUMBER" ? r.MinValue : null, kind == "NUMBER" ? r.MaxValue : null,
            kind == "TEXT" ? r.RequiredTextValue : null, r.IsFailRule)).ToList();
        return new(c.Code, c.Name, GroupLabel(c.Group), kind, kind == "NUMBER" ? c.Unit : null,
            kind == "NUMBER" ? result?.NumericValue : null, kind == "TEXT" ? result?.TextValue : null,
            kind == "BOOLEAN" ? result?.BooleanValue : null, hasValue,
            hasValue && kind != "BOOLEAN" ? result?.EvaluatedGrade : null, hasValue ? result!.IsPassed : null,
            Basis(c, kind, hasValue), rules);
    }

    private static string Basis(TraceabilityCriterionEvidence c, string kind, bool hasValue)
    {
        if (!hasValue) return "Chưa có kết quả ghi nhận; chưa thể kết luận cho tiêu chí này.";
        if (kind == "BOOLEAN") return "Đánh giá đạt/không đạt theo kết quả đã lưu; tiêu chí này không có hạng chữ.";
        if (kind == "TEXT")
            return "Đối chiếu toàn bộ nội dung với yêu cầu của tiêu chuẩn, bỏ khoảng trắng đầu/cuối của kết quả và không phân biệt chữ hoa/thường. Không khớp yêu cầu thì không có hạng; kết luận bên trên là kết quả đã lưu.";
        if (kind != "NUMBER") return "Chưa có đủ căn cứ trình bày cách đánh giá tiêu chí này.";
        var value = c.Result!.NumericValue!.Value;
        var withinAnyRange = c.Rules.Any(r => (!r.MinValue.HasValue || value >= r.MinValue) && (!r.MaxValue.HasValue || value <= r.MaxValue));
        if (!withinAnyRange)
            return "Số đo không nằm trong khoảng nào của tiêu chuẩn. Quy tắc QC xử lý khoảng trống bằng cách xét hạng A đến E, chọn quy tắc đầu tiên có cận dưới lớn hơn số đo; nếu không có thì không xếp hạng. Hạng hiển thị là kết quả đã lưu, không có nghĩa số đo nằm trong khoảng của hạng đó.";
        return "Các khoảng bao gồm cả cận dưới và cận trên; cận trống không giới hạn phía đó. Nếu nhiều khoảng cùng khớp, QC xét từ A đến E. Hạng và kết luận hiển thị là kết quả đã lưu.";
    }

    private static string GroupLabel(string group) => group.ToUpperInvariant() switch
    {
        "SENSORY" => "Cảm quan",
        "LAB" => "Kiểm nghiệm",
        "ENVIRONMENT" => "Điều kiện môi trường",
        _ => "Tiêu chí khác"
    };
}
