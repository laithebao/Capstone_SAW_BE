using System.Globalization;
using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SAW.Application.Features.InspectionStandards;
namespace SAW.Infrastructure.Inspection;

public sealed class InspectionImportLimits
{
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxCriteria = 200;
    public const int MaxRules = 1000;
    public const int MaxUnpackedBytes = 25 * 1024 * 1024;
}
public sealed record ExcelIssue(string Sheet, uint Row, string Column, string? Criterion, string Message);
public sealed record WorkbookPreview(CreateInspectionStandardRequest? Request, string? CropCode, IReadOnlyList<ExcelIssue> Errors);

public static class InspectionWorkbook
{
    public const string Guide = "Hướng dẫn", Info = "Thông tin bộ tiêu chuẩn", Criteria = "Danh sách tiêu chí", Rules = "Quy tắc phân hạng";
    private static readonly string[] InfoColumns = ["StandardCode", "StandardName", "CropCode", "EffectiveFrom", "Description", "VersionNo"];
    private static readonly string[] CriteriaColumns = ["CriterionCode", "CriterionName", "Group", "DataType", "Unit", "IsRequired", "IsCritical"];
    private static readonly string[] RuleColumns = ["CriterionCode", "Grade", "MinValue", "MaxValue", "RequiredTextValue", "IsFailRule"];

    public static byte[] Template(string cropCode)
    {
        using var stream = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var part = doc.AddWorkbookPart(); part.Workbook = new Workbook(new Sheets());
            var styles = part.AddNewPart<WorkbookStylesPart>();
            Font TemplateFont(bool header)
            {
                var font = new Font();
                font.AddChild(new FontName { Val = "Calibri" }, true);
                font.AddChild(new FontSize { Val = 11 }, true);
                if (header) { font.AddChild(new Bold(), true); font.AddChild(new Color { Rgb = "FFFFFFFF" }, true); }
                return font;
            }
            styles.Stylesheet = new Stylesheet(
                new NumberingFormats(new NumberingFormat { NumberFormatId = 164, FormatCode = "dd/mm/yyyy" }),
                new Fonts(TemplateFont(false), TemplateFont(true)),
                new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 }), new Fill(new PatternFill(new ForegroundColor { Rgb = "FF047857" }, new BackgroundColor { Indexed = 64 }) { PatternType = PatternValues.Solid })),
                new Borders(new Border()), new CellStyleFormats(new CellFormat()),
                new CellFormats(new CellFormat(new Alignment { WrapText = true, Vertical = VerticalAlignmentValues.Top }), new CellFormat { NumberFormatId = 164, ApplyNumberFormat = true }, new CellFormat { NumberFormatId = 2, ApplyNumberFormat = true }, new CellFormat(new Alignment { WrapText = true, Vertical = VerticalAlignmentValues.Center }) { FontId = 1, FillId = 2, ApplyFont = true, ApplyFill = true, ApplyAlignment = true }),
                new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }));
            styles.Stylesheet.Save();
            void Sheet(string name, IEnumerable<string[]> rows, params (string Range, string Values)[] lists)
            {
                var ws = part.AddNewPart<WorksheetPart>(); var data = new SheetData(); uint rowIndex = 0;
                foreach (var values in rows)
                {
                    var row = new Row { RowIndex = ++rowIndex, Height = rowIndex == 1 ? 32 : name == Guide ? 82 : 38, CustomHeight = true };
                    for (var i = 0; i < values.Length; i++)
                    {
                        var cell = new Cell { CellReference = $"{(char)('A' + i)}{rowIndex}", DataType = CellValues.InlineString, InlineString = new InlineString(new Text(values[i])) };
                        if (rowIndex == 1) cell.StyleIndex = 3;
                        if (rowIndex > 1 && name == Info && i == 3)
                        {
                            cell.InlineString = null; cell.DataType = CellValues.Number; cell.StyleIndex = 1;
                            cell.CellValue = new CellValue(DateTime.ParseExact(values[i], "dd/MM/yyyy", CultureInfo.InvariantCulture).ToOADate().ToString(CultureInfo.InvariantCulture));
                        }
                        if (rowIndex > 1 && name == Rules && i is 2 or 3 && values[i] != "")
                        { cell.InlineString = null; cell.DataType = CellValues.Number; cell.StyleIndex = 2; cell.CellValue = new CellValue(values[i]); }
                        row.Append(cell);
                    }
                    data.Append(row);
                }
                var view = new SheetView { WorkbookViewId = 0 };
                view.Append(new Pane { VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen });
                var columns = new Columns();
                for (uint i = 1; i <= 8; i++) columns.Append(new Column { Min = i, Max = i, Width = name == Guide ? i == 1 ? 24 : i == 2 ? 32 : 48 : i == 2 ? 42 : 26, CustomWidth = true });
                ws.Worksheet = new Worksheet(new SheetViews(view), columns, data);
                if (lists.Length > 0)
                {
                    var validations = new DataValidations();
                    foreach (var list in lists) validations.Append(new DataValidation(new Formula1('"' + list.Values + '"')) { Type = DataValidationValues.List, SequenceOfReferences = new ListValue<StringValue> { InnerText = list.Range }, AllowBlank = false, ShowErrorMessage = true, Error = "Chọn giá trị trong danh sách." });
                    ws.Worksheet.Append(validations);
                }
                part.Workbook.GetFirstChild<Sheets>()!.Append(new Sheet { Id = part.GetIdOfPart(ws), SheetId = (uint)part.Workbook.GetFirstChild<Sheets>()!.ChildElements.Count + 1, Name = name });
                ws.Worksheet.Save();
            }
            Sheet(Guide, GuideRows());
            Sheet(Info, new[] { InfoColumns, new[] { $"STD_MAU_{DateTime.UtcNow:yyyyMMddHHmmss}", "Bộ tiêu chuẩn minh họa", cropCode, DateTime.UtcNow.AddHours(7).ToString("dd/MM/yyyy"), "Dữ liệu hướng dẫn, cần thay bằng tiêu chuẩn đã được duyệt.", "1" } });
            Sheet(Criteria, new[] { CriteriaColumns,
                new[] { "NUM_EX", "Chỉ số số minh họa", "ENVIRONMENT", "NUMBER", "đơn vị", "TRUE", "FALSE" },
                new[] { "TEXT_EX", "Cảm quan minh họa", "SENSORY", "TEXT", "", "TRUE", "TRUE" },
                new[] { "BOOL_EX", "Kiểm tra đạt/không đạt", "LAB", "BOOLEAN", "", "TRUE", "TRUE" }
            }, ("C2:C201", "ENVIRONMENT,LAB,SENSORY"), ("D2:D201", "NUMBER,TEXT,BOOLEAN"), ("F2:G201", "TRUE,FALSE"));
            Sheet(Rules, new[] { RuleColumns,
                new[] { "NUM_EX", "A", "0", "10", "", "FALSE" }, new[] { "NUM_EX", "B", "10.01", "20", "", "FALSE" },
                new[] { "TEXT_EX", "A", "", "", "Đạt", "FALSE" }, new[] { "TEXT_EX", "B", "", "", "Loại", "TRUE" }
            }, ("B2:B1001", "A,B,C,D,E"), ("F2:F1001", "TRUE,FALSE"));
            part.Workbook.Save();
        }
        return stream.ToArray();
    }

    private static IEnumerable<string[]> GuideRows() => new[] {
        new[] { "Trường / quy tắc", "Ý nghĩa / bắt buộc", "Kiểu / giá trị / để trống", "Ví dụ hợp lệ", "Ràng buộc / lỗi thường gặp" },
        new[] { "Cách sử dụng", "Một bộ tiêu chuẩn trong một workbook", "Giữ nguyên tên sheet và header; chỉ sửa dữ liệu từ dòng 2.", "Tải mẫu → sửa nội dung → nhập xem trước → kiểm tra form → Tạo bộ tiêu chuẩn.", "Sheet này chỉ để đọc, không được import như dữ liệu. Import-preview không ghi DB." },
        new[] { "StandardCode", "Mã bộ tiêu chuẩn; bắt buộc", "Text, tối đa 80 ký tự; không trùng mã đã có.", "STD_GAO_2026", "Không để trống, không tái sử dụng mã bộ tiêu chuẩn khác." },
        new[] { "StandardName", "Tên bộ tiêu chuẩn; bắt buộc", "Text, tối đa 400 ký tự.", "Tiêu chuẩn gạo thu mua", "Không để trống; tên nên mô tả phạm vi áp dụng." },
        new[] { "CropCode", "Mã nông sản; bắt buộc", "Mã chính xác của nông sản đang hoạt động trong SAW.", "Sao chép mã từ quản lý loại nông sản.", "Không nhập tên nông sản hoặc mã Category thay cho CropCode." },
        new[] { "EffectiveFrom", "Ngày hiệu lực; bắt buộc trong mẫu", "Ngày Excel hoặc dd/MM/yyyy, yyyy-MM-dd; form tạo yêu cầu từ hôm nay.", "26/11/2026", "Không dùng chế độ ngày 1904; không nhập ngày bằng công thức." },
        new[] { "Description", "Mô tả; tùy chọn", "Text, tối đa 2.000 ký tự; có thể để trống.", "Áp dụng cho gạo thu mua trong kỳ.", "Không đặt hướng dẫn vào các dòng dữ liệu bổ sung." },
        new[] { "VersionNo", "Phiên bản tạo mới; bắt buộc", "Số 1. Phiên bản tiếp theo do Backend quản lý.", "1", "Không đổi thành 2 để tạo revision của bộ có sẵn qua import này." },
        new[] { "CriterionCode", "Mã tiêu chí; bắt buộc ở cả hai sheet", "Text, tối đa 100 ký tự; duy nhất trong phiên bản, không phân biệt hoa/thường.", "MOISTURE", "Rule phải tham chiếu đúng mã ở Danh sách tiêu chí. Không trùng mã sau khi trim." },
        new[] { "CriterionName", "Tên tiêu chí; bắt buộc", "Text, tối đa 300 ký tự.", "Độ ẩm", "Không để trống." },
        new[] { "Group", "Nhóm tiêu chí", "ENVIRONMENT, LAB hoặc SENSORY. Backend mặc định LAB nếu để trống.", "LAB", "Không nhập nhãn tiếng Việt thay cho enum. Nên chọn dropdown để khai báo rõ nhóm." },
        new[] { "DataType", "Kiểu đánh giá; bắt buộc", "NUMBER, TEXT hoặc BOOLEAN.", "NUMBER", "NUMBER đo số; TEXT so khớp cảm quan; BOOLEAN đánh giá đạt/không đạt." },
        new[] { "Unit", "Đơn vị; tùy chọn", "Text, tối đa 100 ký tự; để trống nếu không có.", "%", "Không đặt đơn vị vào ô MinValue/MaxValue." },
        new[] { "IsRequired", "Có bắt buộc thực hiện tiêu chí khi kiểm định không", "TRUE hoặc FALSE; bắt buộc. Parser cũng nhận 1/0.", "TRUE", "Không đồng nghĩa IsCritical. Tiêu chí bắt buộc chưa chắc có quyền từ chối lô." },
        new[] { "IsCritical", "Tiêu chí nghiêm trọng, có thể dẫn đến từ chối lô", "TRUE hoặc FALSE; bắt buộc.", "TEXT_EX: TRUE; NUM_EX: FALSE", "Với NUMBER/TEXT: TRUE cần ít nhất một IsFailRule=TRUE. Không đánh dấu critical chỉ để vượt validation." },
        new[] { "Grade", "Hạng chất lượng của rule; bắt buộc khi có rule", "A, B, C, D, E. A tốt nhất; mỗi hạng chỉ một rule trong tiêu chí.", "A, B, C", "Phải bắt đầu A và liên tục: A; AB; ABC; ABCD; ABCDE. Sai: BC, AC, ACD." },
        new[] { "MinValue / MaxValue", "Biên khoảng số cho NUMBER", "Ít nhất một biên; biên trống là không giới hạn. Min ≤ Max. Số dùng dấu chấm thập phân, không phân cách nghìn.", "A: [0,10]; B: [10.01,20]", "Khoảng đóng: [0,10] và [10,20] bị chồng tại 10. Giá trị ngoài mọi khoảng không tự thuộc hạng tốt nhất." },
        new[] { "RequiredTextValue", "Giá trị chấp nhận cho TEXT", "Text không rỗng với từng rule TEXT; tối đa 300 ký tự. Để trống với NUMBER/BOOLEAN.", "A: Đạt; B: Loại", "Không nhập Min/Max để thay thế giá trị TEXT. Chỉ dùng giá trị nghiệp vụ đã được duyệt." },
        new[] { "IsFailRule", "Hạng là ngưỡng từ chối cho tiêu chí nghiêm trọng", "TRUE hoặc FALSE; bắt buộc khi có rule.", "IsCritical=TRUE và hạng B=TRUE trong ví dụ TEXT.", "IsFailRule=TRUE chỉ được phép khi IsCritical=TRUE. Hạng A không được là ngưỡng từ chối." },
        new[] { "Ngưỡng từ chối liên tục", "Các hạng tệ hơn ngưỡng đều phải từ chối", "Áp dụng cho các hạng đã được định nghĩa.", "Nếu D=TRUE và có E thì E phải TRUE.", "Sai: C=TRUE, D=FALSE. Không bắt buộc tự tạo thêm hạng chưa có." },
        new[] { "NUMBER – ví dụ", "Cần ít nhất một Grade Rule", "NUM_EX: NUMBER, ENVIRONMENT, IsRequired=TRUE, IsCritical=FALSE.", "A [0,10] FALSE; B [10.01,20] FALSE.", "Sai: thiếu cả Min/Max; Min>Max; hai khoảng chồng nhau; IsFailRule=TRUE khi không critical." },
        new[] { "TEXT – ví dụ", "Cần ít nhất một Grade Rule", "TEXT_EX: TEXT, SENSORY, IsRequired=TRUE, IsCritical=TRUE.", "A: Đạt, FALSE; B: Loại, TRUE.", "Sai: RequiredTextValue rỗng; critical nhưng không có rule từ chối; hạng A từ chối." },
        new[] { "BOOLEAN – ví dụ", "Đạt/không đạt được engine đánh giá trực tiếp", "BOOL_EX: BOOLEAN, LAB, IsRequired=TRUE, IsCritical=TRUE. Không cần nhập dòng ở sheet Rules.", "Không tạo rule cho BOOL_EX; khi QC nhập giá trị Boolean.", "Bắt buộc IsCritical=TRUE và không có GradeRules. Không dùng các cột Min/Max hay RequiredTextValue để thay giá trị Boolean." },
        new[] { "Giới hạn và bảo mật", "Một file xlsx tối đa 5 MB", "1–200 tiêu chí; tối đa 1.000 rule; tối đa 25 MB giải nén, 1.000 ZIP entries.", "Dữ liệu giá trị thuần, không công thức.", "Không formula, macro, external link, header/cột lạ hoặc sheet bị đổi tên. TRUE/FALSE theo dropdown." },
        new[] { "Ý nghĩa dữ liệu mẫu", "Chỉ minh họa cấu trúc và validation", "Không phải ngưỡng an toàn thực phẩm hay tiêu chuẩn pháp lý.", "Thay ví dụ bằng tiêu chuẩn đã được phê duyệt.", "Không áp dụng ngưỡng mẫu vào lô thực tế khi chưa có tiêu chuẩn nghiệp vụ hợp lệ." }
    };

    public static WorkbookPreview Parse(byte[] bytes)
    {
        var issues = new List<ExcelIssue>();
        void Error(string sheet, uint row, string column, string? code, string message) => issues.Add(new(sheet, row, column, code, message));
        try
        {
            if (bytes.Length > InspectionImportLimits.MaxFileBytes) throw new InvalidDataException("File vượt 5 MB.");
            using var source = new MemoryStream(bytes);
            using (var zip = new ZipArchive(source, ZipArchiveMode.Read, true))
            {
                if (zip.Entries.Count > 1000 || zip.Entries.Sum(x => x.Length) > InspectionImportLimits.MaxUnpackedBytes) throw new InvalidDataException("Nội dung giải nén quá lớn.");
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("externalLinks", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Không hỗ trợ macro hoặc external link.");
                    if (entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                    {
                        using var reader = System.Xml.XmlReader.Create(entry.Open(), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, MaxCharactersInDocument = InspectionImportLimits.MaxUnpackedBytes });
                        while (reader.Read()) if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.GetAttribute("TargetMode") == "External") throw new InvalidDataException("Không hỗ trợ liên kết ngoài.");
                    }
                }
            }
            source.Position = 0;
            using var doc = SpreadsheetDocument.Open(source, false, new OpenSettings { MaxCharactersInPart = InspectionImportLimits.MaxUnpackedBytes });
            var wb = doc.WorkbookPart ?? throw new InvalidDataException("Thiếu workbook.");
            var workbook = wb.Workbook ?? throw new InvalidDataException("Thiếu workbook.");
            if (workbook.WorkbookProperties?.Date1904?.Value == true) throw new InvalidDataException("Không hỗ trợ chế độ ngày 1904. Vui lòng dùng file mẫu của hệ thống.");
            foreach (var ws in wb.WorksheetParts) if ((ws.Worksheet ?? throw new InvalidDataException("Thiếu worksheet.")).Descendants<CellFormula>().Any()) throw new InvalidDataException("Không hỗ trợ công thức Excel.");
            string Value(Cell cell) => cell.DataType?.Value == CellValues.SharedString
                ? (wb.SharedStringTablePart?.SharedStringTable ?? throw new InvalidDataException("Thiếu bảng chuỗi Excel.")).Elements<SharedStringItem>().ElementAt(int.Parse(cell.InnerText, CultureInfo.InvariantCulture)).InnerText.Trim()
                : (cell.InlineString?.InnerText ?? cell.CellValue?.InnerText ?? "").Trim();
            List<(uint Row, string[] Cells)> Read(string name, string[] columns)
            {
                var sheet = workbook.GetFirstChild<Sheets>()?.Elements<Sheet>().SingleOrDefault(x => x.Name?.Value == name);
                if (sheet?.Id?.Value is null) { Error(name, 1, "", null, "Thiếu sheet."); return []; }
                var rows = ((WorksheetPart)wb.GetPartById(sheet.Id.Value)).Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>().ToList() ?? [];
                var result = new List<(uint, string[])>();
                foreach (var row in rows)
                {
                    var values = new string[columns.Length]; Array.Fill(values, "");
                    foreach (var cell in row.Elements<Cell>())
                    {
                        var reference = cell.CellReference?.Value ?? ""; var letters = new string(reference.TakeWhile(char.IsLetter).ToArray());
                        var index = letters.Aggregate(0, (n, ch) => n * 26 + ch - 'A' + 1) - 1;
                        if (index >= 0 && index < values.Length) values[index] = Value(cell);
                        else if (!string.IsNullOrWhiteSpace(Value(cell))) Error(name, row.RowIndex?.Value ?? 0, reference, null, "Cột không được hỗ trợ.");
                    }
                    if (row.RowIndex?.Value == 1) { if (!values.SequenceEqual(columns)) Error(name, 1, "", null, "Thiếu hoặc sai thứ tự cột: " + string.Join(", ", columns)); }
                    else if (values.Any(x => x.Length > 0)) result.Add((row.RowIndex?.Value ?? 0, values));
                }
                if (!rows.Any(x => x.RowIndex?.Value == 1)) Error(name, 1, "", null, "Thiếu dòng tiêu đề.");
                return result;
            }
            if (!workbook.Descendants<Sheet>().Any(x => x.Name?.Value == Guide)) Error(Guide, 1, "", null, "Thiếu sheet hướng dẫn.");
            var info = Read(Info, InfoColumns); var criteria = Read(Criteria, CriteriaColumns); var rules = Read(Rules, RuleColumns);
            if (info.Count != 1) Error(Info, 2, "", null, "File phải có đúng một bộ tiêu chuẩn.");
            if (criteria.Count is < 1 or > InspectionImportLimits.MaxCriteria) Error(Criteria, 2, "", null, "Cần từ 1 đến 200 tiêu chí.");
            if (rules.Count > InspectionImportLimits.MaxRules) Error(Rules, 2, "", null, "Tối đa 1.000 grade rules.");
            bool Bool(string value, string sheet, uint row, string column, string code)
            {
                if (bool.TryParse(value, out var b)) return b;
                if (value is "0" or "1") return value == "1";
                Error(sheet, row, column, code, "Nhập TRUE hoặc FALSE."); return false;
            }
            decimal? Number(string value, uint row, string column, string code)
            {
                if (value == "") return null;
                if (decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)) return n;
                Error(Rules, row, column, code, "Số thập phân không hợp lệ."); return null;
            }
            var built = new List<SaveInspectionCriterionRequest>();
            foreach (var (row, v) in criteria.Take(InspectionImportLimits.MaxCriteria))
            {
                if (v[0].Length is 0 or > 100) Error(Criteria, row, "CriterionCode", v[0], "Mã tiêu chí bắt buộc, tối đa 100 ký tự.");
                if (v[1].Length is 0 or > 300) Error(Criteria, row, "CriterionName", v[0], "Tên tiêu chí bắt buộc, tối đa 300 ký tự.");
                if (v[4].Length > 100) Error(Criteria, row, "Unit", v[0], "Đơn vị tối đa 100 ký tự.");
                var gradeRules = rules.Where(x => x.Cells[0].Equals(v[0], StringComparison.OrdinalIgnoreCase)).Select(x => new SaveGradeRuleRequest(x.Cells[1], Number(x.Cells[2], x.Row, "MinValue", v[0]), Number(x.Cells[3], x.Row, "MaxValue", v[0]), x.Cells[4] == "" ? null : x.Cells[4], Bool(x.Cells[5], Rules, x.Row, "IsFailRule", v[0]))).ToList();
                built.Add(new(v[0], v[1], v[2], v[3], v[4] == "" ? null : v[4], Bool(v[5], Criteria, row, "IsRequired", v[0]), Bool(v[6], Criteria, row, "IsCritical", v[0]), gradeRules));
            }
            foreach (var (row, v) in rules)
            {
                if (!built.Any(c => c.Code.Equals(v[0], StringComparison.OrdinalIgnoreCase))) Error(Rules, row, "CriterionCode", v[0], "Không tìm thấy tiêu chí.");
                if (v[4].Length > 300) Error(Rules, row, "RequiredTextValue", v[0], "Giá trị tối đa 300 ký tự.");
                var criterion = built.FirstOrDefault(c => c.Code.Equals(v[0], StringComparison.OrdinalIgnoreCase));
                if (criterion is { IsCritical: false } && !criterion.DataType.Equals("BOOLEAN", StringComparison.OrdinalIgnoreCase)
                    && (v[5].Equals("TRUE", StringComparison.OrdinalIgnoreCase) || v[5] == "1"))
                    Error(Rules, row, "IsFailRule", v[0], "IsFailRule = TRUE chỉ được phép khi IsCritical = TRUE. Chỉ dùng ngưỡng từ chối cho tiêu chí thực sự nghiêm trọng.");
            }
            if (info.Count != 1 || issues.Count > 0) return new(null, null, issues);
            var head = info[0].Cells;
            DateOnly date;
            if (!DateOnly.TryParseExact(head[3], ["dd/MM/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                if (double.TryParse(head[3], CultureInfo.InvariantCulture, out var oa) && oa is > 1 and < 100000) date = DateOnly.FromDateTime(DateTime.FromOADate(oa));
                else Error(Info, info[0].Row, "EffectiveFrom", null, "Ngày không hợp lệ, dùng dd/MM/yyyy.");
            }
            if (head[5] != "1") Error(Info, info[0].Row, "VersionNo", null, "Phiên bản tạo mới phải là 1; phiên bản tiếp theo do backend quản lý.");
            try { InspectionStandardService.ValidateCriteria(built); }
            catch (SAW.Application.Exceptions.BadRequestException e)
            {
                var match = criteria.FirstOrDefault(x => e.Message.Contains(x.Cells[0], StringComparison.Ordinal));
                Error(Criteria, match.Row == 0 ? 2 : match.Row, "GradeRules", match.Cells?.FirstOrDefault(), e.Message);
            }
            return new(new(0, head[0], head[1], head[4], 1, date, built), head[2], issues);
        }
        catch (Exception e) when (e is InvalidDataException or OpenXmlPackageException or FormatException or System.Xml.XmlException or ArgumentException or InvalidOperationException)
        { Error("File", 0, "", null, e is InvalidDataException ? e.Message : "File xlsx không hợp lệ."); return new(null, null, issues); }
    }
}
