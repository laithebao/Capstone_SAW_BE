using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using SAW.Domain.Common;
using SAW.Domain.Entities;
using SAW.Infrastructure.Inspection;

namespace SAW.Test.Infrastructure.BigUpdate01;

public sealed class CropWorkbookTests
{
    internal static Category[] Categories(int root = 1, int child = 2) => [
        new() { CategoryId = root, Code = "LT", Name = "Lương thực" },
        new() { CategoryId = child, ParentId = root, Code = "NGO", Name = "Ngô" }];

    internal static byte[] Workbook(IReadOnlyList<Category> categories, params string[][] rows)
    {
        using var stream = new MemoryStream(); stream.Write(CropTypeWorkbook.Template(categories)); stream.Position = 0;
        using (var doc = SpreadsheetDocument.Open(stream, true))
        {
            var workbook = doc.WorkbookPart!;
            var part = (WorksheetPart)workbook.GetPartById(workbook.Workbook!.Sheets!.Elements<Sheet>().Single(s => s.Name == CropTypeWorkbook.DataSheet).Id!.Value!);
            var data = part.Worksheet!.GetFirstChild<SheetData>()!; uint index = 1;
            foreach (var values in rows)
            {
                var row = new Row { RowIndex = ++index };
                for (var i = 0; i < values.Length; i++) row.Append(new Cell { CellReference = $"{(char)('A' + i)}{index}", DataType = CellValues.InlineString, InlineString = new InlineString(new Text(values[i])) });
                data.Append(row);
            }
            part.Worksheet.Save();
        }
        return stream.ToArray();
    }

    [Fact]
    public void TemplateIsRealValidWorkbookWithCategoryDropdownsAndNoSampleCrops()
    {
        using var stream = new MemoryStream(CropTypeWorkbook.Template(Categories())); using var doc = SpreadsheetDocument.Open(stream, false);
        Assert.Empty(new OpenXmlValidator().Validate(doc));
        Assert.Equal(2, doc.WorkbookPart!.Workbook!.DefinedNames!.ChildElements.Count);
        var parsed = CropTypeWorkbook.Parse(stream.ToArray(), Categories(), DateTime.UtcNow);
        Assert.Empty(parsed.Rows); Assert.Contains(parsed.Errors, e => e.Message.Contains("chưa có"));
    }

    [Fact]
    public void RowsUseVietnameseDateAndCapitalizedNamesWithoutChangingAcronyms()
    {
        var result = CropTypeWorkbook.Parse(Workbook(Categories(), ["LT", "NGO", " ngô ST25 ", "", ""]), Categories(), new DateTime(2026, 11, 26, 18, 0, 0));
        Assert.Empty(result.Errors); var item = Assert.Single(result.Rows);
        Assert.Equal("Ngô ST25", item.Request.Name); Assert.Equal("LT_NGO_ST25_27112026", item.Request.Code); Assert.True(item.Request.IsActive);
        Assert.Equal("Đậu xanh", DisplayName.CapitalizeFirst(" đậu xanh "));
    }

    [Fact]
    public void InvalidDuplicatesHierarchyStatusAndRowLimitAreRejected()
    {
        var duplicate = CropTypeWorkbook.Parse(Workbook(Categories(), ["LT", "NGO", "Ngô ngọt", "SAME", "TRUE"], ["LT", "NGO", "ngô ngọt", "same", "bad"]), Categories(), DateTime.UtcNow);
        Assert.Contains(duplicate.Errors, x => x.Column == "Name"); Assert.Contains(duplicate.Errors, x => x.Column == "Code"); Assert.Contains(duplicate.Errors, x => x.Column == "IsActive");
        Assert.NotEmpty(CropTypeWorkbook.Parse(Workbook(Categories(), ["UNKNOWN", "NGO", "Tên", "CODE", ""]), Categories(), DateTime.UtcNow).Errors);
        var many = Enumerable.Range(0, 201).Select(i => new[] { "LT", "NGO", "Ngô " + i, "CODE_" + i, "" }).ToArray();
        Assert.Contains(CropTypeWorkbook.Parse(Workbook(Categories(), many), Categories(), DateTime.UtcNow).Errors, x => x.Message.Contains("200"));
        Assert.NotEmpty(CropTypeWorkbook.Parse([1, 2, 3], Categories(), DateTime.UtcNow).Errors);
    }
}
