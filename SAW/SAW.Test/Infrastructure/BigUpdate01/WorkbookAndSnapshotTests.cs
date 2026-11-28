using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using SAW.Application.Features.InspectionStandards;
using SAW.Domain.Entities;
using SAW.Infrastructure.Contracts;
using SAW.Infrastructure.Inspection;
namespace SAW.Test.Infrastructure.BigUpdate01;
public sealed class WorkbookAndSnapshotTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public void BooleanImportUsesTheSameCriticalGateAndRejectsGradeRules(bool critical, bool hasRules, bool valid)
    {
        using var stream = new MemoryStream(); stream.Write(InspectionWorkbook.Template("CROP")); stream.Position = 0;
        using (var doc = SpreadsheetDocument.Open(stream, true))
        {
            var workbook = doc.WorkbookPart!;
            WorksheetPart Part(string name) => (WorksheetPart)workbook.GetPartById(workbook.Workbook!.Sheets!.Elements<Sheet>().Single(x => x.Name == name).Id!.Value!);
            var criteria = Part(InspectionWorkbook.Criteria);
            criteria.Worksheet!.Descendants<Cell>().Single(x => x.CellReference == "G4").InlineString = new InlineString(new Text(critical ? "TRUE" : "FALSE"));
            criteria.Worksheet.Save();
            if (hasRules)
            {
                var rules = Part(InspectionWorkbook.Rules); var row = new Row { RowIndex = 6 };
                var values = new[] { "BOOL_EX", "A", "", "", "", "FALSE" };
                for (var i = 0; i < values.Length; i++) row.Append(new Cell { CellReference = $"{(char)('A' + i)}6", DataType = CellValues.InlineString, InlineString = new InlineString(new Text(values[i])) });
                rules.Worksheet!.GetFirstChild<SheetData>()!.Append(row); rules.Worksheet.Save();
            }
        }
        var parsed = InspectionWorkbook.Parse(stream.ToArray());
        if (valid) { Assert.Empty(parsed.Errors); Assert.NotNull(parsed.Request); }
        else Assert.Contains(parsed.Errors, x => x.Criterion == "BOOL_EX" && x.Message.Contains("BOOLEAN"));
    }

    [Fact]
    public void RealWorkbookOpensAndAllThreeExamplesPassTheExistingValidator()
    {
        var bytes = InspectionWorkbook.Template("NC_GAO_ST25_26092026");
        using var doc = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        var schemaErrors = new OpenXmlValidator().Validate(doc).ToList();
        Assert.True(schemaErrors.Count == 0, string.Join("\n", schemaErrors.Select(e => e.Description + " @ " + e.Path?.XPath)));
        var preview = InspectionWorkbook.Parse(bytes);
        Assert.Empty(preview.Errors); Assert.NotNull(preview.Request);
        Assert.Equal(new[] { "NUMBER", "TEXT", "BOOLEAN" }, preview.Request.Criteria.Select(c => c.DataType));
        InspectionStandardService.ValidateCriteria(preview.Request.Criteria);
        Assert.Equal(1, preview.Request.VersionNo);
    }
    [Fact]
    public void InvalidCriticalFailRuleReportsActualSheetRowAndColumn()
    {
        using var stream = new MemoryStream(); stream.Write(InspectionWorkbook.Template("CROP")); stream.Position = 0;
        using (var doc = SpreadsheetDocument.Open(stream, true))
        {
            var workbook = doc.WorkbookPart!;
            var sheet = workbook.Workbook!.Sheets!.Elements<Sheet>().Single(x => x.Name == InspectionWorkbook.Criteria);
            var part = (WorksheetPart)workbook.GetPartById(sheet.Id!.Value!);
            var cell = part.Worksheet!.Descendants<Cell>().Single(x => x.CellReference == "G3");
            cell.InlineString = new InlineString(new Text("FALSE")); part.Worksheet!.Save();
        }
        var result = InspectionWorkbook.Parse(stream.ToArray());
        Assert.Contains(result.Errors, x => x.Sheet == InspectionWorkbook.Rules && x.Row == 5 && x.Column == "IsFailRule" && x.Criterion == "TEXT_EX" && x.Message.Contains("IsCritical"));
        Assert.Null(result.Request);
    }

    [Fact]
    public void FormulaAndMalformedFilesAreRejectedWithoutAnImportRequest()
    {
        Assert.NotEmpty(InspectionWorkbook.Parse(Encoding.UTF8.GetBytes("not-xlsx")).Errors);
        using var stream = new MemoryStream(); stream.Write(InspectionWorkbook.Template("CROP")); stream.Position = 0;
        using (var doc = SpreadsheetDocument.Open(stream, true)) { doc.WorkbookPart!.WorksheetParts.First().Worksheet!.Descendants<Cell>().First().Append(new CellFormula("1+1")); }
        var parsed = InspectionWorkbook.Parse(stream.ToArray()); Assert.NotEmpty(parsed.Errors); Assert.Null(parsed.Request);
    }
    [Fact]
    public void ContractSnapshotKeepsTheSentContentAndDoesNotInventLegalRepresentative()
    {
        var contract = new SupplyContract { ContractCode = "HD-TEST", Revision = 1, DeliveryQuantityKg = 500, UnitPriceVndPerKg = 1000.10m, StartDate = new(2026, 11, 25), EndDate = new(2026, 12, 25) };
        var supplier = new Supplier { SupplierName = "Supplier original", ContactPerson = "Contact only" };
        var crop = new CropType { CropCode = "OLD_CODE", CropName = "Gạo" };
        var snapshot = ContractDocument.Build(contract, supplier, crop, null);
        supplier.SupplierName = "Changed"; crop.CropCode = "NEW_CODE"; contract.UnitPriceVndPerKg = 2000;
        Assert.Contains("Supplier original", snapshot.Content); Assert.Contains("OLD_CODE", snapshot.Content);
        Assert.DoesNotContain("NEW_CODE", snapshot.Content); Assert.Contains("Người đại diện pháp lý: Chưa cung cấp", snapshot.Content);
        Assert.Equal(64, snapshot.ContentHash.Length);
        contract.Revision = 2; Assert.NotEqual(snapshot.ContentHash, ContractDocument.Build(contract, supplier, crop, null).ContentHash);
    }
}
