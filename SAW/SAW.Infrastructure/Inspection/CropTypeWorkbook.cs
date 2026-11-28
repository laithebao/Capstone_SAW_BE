using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SAW.Application.Features.CropTypes;
using SAW.Domain.Common;
using SAW.Domain.Entities;

namespace SAW.Infrastructure.Inspection;

public sealed record CropImportIssue(uint Row, string Column, string Message);
public sealed record CropImportRow(uint Row, string ParentCategory, string ChildCategory, SaveCropTypeBasicsRequest Request);
public sealed record CropImportPreview(IReadOnlyList<CropImportRow> Rows, IReadOnlyList<CropImportIssue> Errors);

public static class CropTypeWorkbook
{
    public const int MaxRows = 200;
    public const string DataSheet = "Loại nông sản";
    private static readonly string[] Headers = ["ParentCode", "ChildCode", "Name", "Code", "IsActive"];

    public static byte[] Template(IReadOnlyList<Category> categories)
    {
        using var stream = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var book = doc.AddWorkbookPart(); book.Workbook = new Workbook(new Sheets());
            void Sheet(string name, IEnumerable<string[]> rows)
            {
                var part = book.AddNewPart<WorksheetPart>(); var data = new SheetData(); uint index = 0;
                foreach (var values in rows)
                {
                    var row = new Row { RowIndex = ++index };
                    for (var i = 0; i < values.Length; i++) row.Append(new Cell
                    { CellReference = $"{(char)('A' + i)}{index}", DataType = CellValues.InlineString, InlineString = new InlineString(new Text(values[i])) });
                    data.Append(row);
                }
                part.Worksheet = new Worksheet(new SheetViews(new SheetView(new Pane
                { VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen }) { WorkbookViewId = 0 }),
                    new Columns(new Column { Min = 1, Max = 5, Width = 32, CustomWidth = true }), data);
                book.Workbook.Sheets!.Append(new Sheet { Id = book.GetIdOfPart(part), SheetId = (uint)book.Workbook.Sheets.ChildElements.Count + 1, Name = name });
                part.Worksheet.Save();
            }
            Sheet("Hướng dẫn", [
                ["Trường", "Hướng dẫn"], ["Cách nhập", "Điền sheet Loại nông sản từ dòng 2; tối đa 200 loại. Xem trước rồi xác nhận để tạo toàn bộ."],
                ["ParentCode / ChildCode", "Bắt buộc; chọn mã từ sheet Danh mục. Danh mục con phải thuộc đúng cha và cả hai đang hoạt động."],
                ["Name", "Bắt buộc, tối đa 150 ký tự. Chữ đầu tự viết hoa; tên không trùng trong file hoặc database."],
                ["Code", "Có thể để trống để tự sinh theo danh mục, tên và ngày Việt Nam. Mã tùy chỉnh: A-Z, 0-9, _, tối đa 50 ký tự; không trùng."],
                ["IsActive", "Để trống hoặc TRUE: hoạt động; FALSE: ngừng hoạt động."],
                ["Lưu dữ liệu", "Chỉ tạo mới, không ghi đè loại đã có. Có lỗi thì không lưu dòng nào. Không dùng công thức hoặc macro."]]);
            Sheet(DataSheet, [Headers]);
            var parents = categories.Where(x => x.ParentId is null && x.IsActive).OrderBy(x => x.Code).ToArray();
            var children = categories.Where(x => x.ParentId != null && x.IsActive && parents.Any(p => p.CategoryId == x.ParentId)).OrderBy(x => x.Code).ToArray();
            var reference = new List<string[]> { new[] { "ParentCode", "ParentName", "ChildCode", "ChildName", "ChildParentCode" } };
            for (var i = 0; i < Math.Max(parents.Length, children.Length); i++) reference.Add([
                i < parents.Length ? parents[i].Code : "", i < parents.Length ? parents[i].Name : "",
                i < children.Length ? children[i].Code : "", i < children.Length ? children[i].Name : "",
                i < children.Length ? parents.Single(p => p.CategoryId == children[i].ParentId).Code : ""]);
            Sheet("Danh mục", reference);
            book.Workbook.Append(new DefinedNames(
                new DefinedName { Name = "ParentCodes", Text = $"'Danh mục'!$A$2:$A${Math.Max(2, parents.Length + 1)}" },
                new DefinedName { Name = "ChildCodes", Text = $"'Danh mục'!$C$2:$C${Math.Max(2, children.Length + 1)}" }));
            var dataPart = (WorksheetPart)book.GetPartById(book.Workbook.Sheets.Elements<Sheet>().Single(x => x.Name == DataSheet).Id!.Value!);
            var validations = new DataValidations();
            foreach (var (range, formula) in new[] { ("A2:A201", "ParentCodes"), ("B2:B201", "ChildCodes"), ("E2:E201", "\"TRUE,FALSE\"") })
                validations.Append(new DataValidation(new Formula1(formula)) { Type = DataValidationValues.List,
                    SequenceOfReferences = new ListValue<StringValue> { InnerText = range }, AllowBlank = true, ShowErrorMessage = true, Error = "Chọn giá trị từ danh sách." });
            dataPart.Worksheet.Append(validations); dataPart.Worksheet.Save(); book.Workbook.Save();
        }
        return stream.ToArray();
    }

    public static string GenerateCode(Category child, Category parent, string name, DateTime utcNow)
    {
        static string[] Words(string value) => Regex.Matches(new string(value.Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .Replace('đ', 'D').Replace('Đ', 'D').ToUpperInvariant(), "[A-Z0-9]+").Select(m => m.Value).ToArray();
        var words = Words(name); var prefix = Words(child.Name);
        var remainder = words.Take(prefix.Length).SequenceEqual(prefix) ? words.Skip(prefix.Length).ToArray() : words;
        var suffix = string.Concat(remainder.Length > 0 ? remainder : words);
        var date = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));
        return $"{parent.Code}_{child.Code}_{suffix}_{date:ddMMyyyy}";
    }

    public static CropImportPreview Parse(byte[] bytes, IReadOnlyList<Category> categories, DateTime utcNow)
    {
        var errors = new List<CropImportIssue>(); var result = new List<CropImportRow>();
        void Error(uint row, string column, string message) => errors.Add(new(row, column, message));
        try
        {
            if (bytes.Length is 0 or > InspectionImportLimits.MaxFileBytes) throw new InvalidDataException("Chỉ nhận xlsx, tối đa 5 MB.");
            using var stream = new MemoryStream(bytes);
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
            {
                if (zip.Entries.Count > 2000 || zip.Entries.Sum(x => x.Length) > InspectionImportLimits.MaxUnpackedBytes
                    || zip.Entries.Any(x => x.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("File quá lớn sau giải nén hoặc chứa macro.");
            }
            stream.Position = 0;
            using var doc = SpreadsheetDocument.Open(stream, false, new OpenSettings { MaxCharactersInPart = InspectionImportLimits.MaxUnpackedBytes });
            var book = doc.WorkbookPart ?? throw new InvalidDataException("Thiếu workbook.");
            if (book.WorksheetParts.Any(x => x.Worksheet.Descendants<CellFormula>().Any())) throw new InvalidDataException("Không nhận công thức Excel.");
            var sheet = book.Workbook.Sheets?.Elements<Sheet>().SingleOrDefault(x => x.Name == DataSheet)
                ?? throw new InvalidDataException($"Thiếu sheet {DataSheet}.");
            var data = ((WorksheetPart)book.GetPartById(sheet.Id!.Value!)).Worksheet.GetFirstChild<SheetData>()
                ?? throw new InvalidDataException("Thiếu dữ liệu.");
            string Value(Cell cell) => cell.DataType?.Value == CellValues.SharedString
                ? (book.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().ElementAt(int.Parse(cell.InnerText, CultureInfo.InvariantCulture)).InnerText ?? "").Trim()
                : (cell.InlineString?.InnerText ?? cell.CellValue?.InnerText ?? "").Trim();
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0; var header = false;
            foreach (var row in data.Elements<Row>())
            {
                var index = row.RowIndex?.Value ?? throw new InvalidDataException("Dòng thiếu vị trí.");
                var values = Enumerable.Repeat("", 5).ToArray(); var seen = new HashSet<int>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var letters = new string((cell.CellReference?.Value ?? "").TakeWhile(char.IsLetter).ToArray());
                    var col = letters.Aggregate(0, (n, ch) => n * 26 + char.ToUpperInvariant(ch) - 'A' + 1) - 1;
                    var value = Value(cell);
                    if (col < 0 || col > 4 || !seen.Add(col)) { if (value.Length > 0) Error(index, letters, "Cột không hợp lệ hoặc trùng."); }
                    else values[col] = value;
                }
                if (index == 1) { header = true; if (!values.SequenceEqual(Headers)) Error(1, "", "Giữ nguyên tiêu đề: " + string.Join(", ", Headers)); continue; }
                if (values.All(string.IsNullOrEmpty)) continue;
                if (++count > MaxRows) { Error(index, "", "Tối đa 200 loại mỗi lần nhập."); break; }
                var parent = categories.SingleOrDefault(x => x.ParentId is null && x.Code.Equals(values[0], StringComparison.OrdinalIgnoreCase));
                var child = categories.SingleOrDefault(x => x.ParentId == parent?.CategoryId && x.ParentId != null && x.Code.Equals(values[1], StringComparison.OrdinalIgnoreCase));
                if (parent is null || child is null || !parent.IsActive || !child.IsActive) { Error(index, "ParentCode/ChildCode", "Mã danh mục không tồn tại, không đúng cha/con hoặc đã ngừng hoạt động."); continue; }
                var name = DisplayName.CapitalizeFirst(values[2]);
                var code = (values[3].Length == 0 ? GenerateCode(child, parent, name, utcNow) : values[3]).ToUpperInvariant();
                if (name.Length is 0 or > 150) Error(index, "Name", "Tên bắt buộc, tối đa 150 ký tự.");
                if (code.Length is 0 or > 50 || !Regex.IsMatch(code, "^[A-Z0-9_]+$")) Error(index, "Code", "Mã gồm A-Z, số, _, tối đa 50 ký tự.");
                if (!names.Add(name)) Error(index, "Name", "Tên bị trùng trong file.");
                if (!codes.Add(code)) Error(index, "Code", "Mã bị trùng trong file.");
                var active = true;
                if (values[4].Length > 0 && !bool.TryParse(values[4], out active)) Error(index, "IsActive", "Nhập TRUE, FALSE hoặc để trống.");
                result.Add(new(index, parent.Name, child.Name, new(code, name, child.CategoryId, active)));
            }
            if (!header) Error(1, "", "Thiếu dòng tiêu đề.");
            if (count == 0) Error(2, "", "File chưa có loại nông sản để tạo.");
        }
        catch (Exception e) when (e is InvalidDataException or OpenXmlPackageException or System.Xml.XmlException or FormatException or ArgumentException or InvalidOperationException or OverflowException)
        { Error(0, "File", "Không đọc được file xlsx hợp lệ. Kiểm tra cấu trúc, giới hạn và nội dung: " + (e is InvalidDataException ? e.Message : "File bị hỏng hoặc sai định dạng.")); }
        return new(result, errors);
    }
}
