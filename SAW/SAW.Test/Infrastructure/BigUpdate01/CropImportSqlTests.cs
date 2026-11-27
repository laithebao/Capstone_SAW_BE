using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SAW.Domain.Entities;
using SAW.Test.Infrastructure.ProductBatches;

namespace SAW.Test.Infrastructure.BigUpdate01;

public sealed class CropImportSqlTests(ReceivingSqlFixture f) : IClassFixture<ReceivingSqlFixture>
{
    private AuthenticationHeaderValue Token(string role) => new("Bearer", new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "uc28-test", "uc28-test", [new("account_id", f.ActorId.ToString()), new(System.Security.Claims.ClaimTypes.Role, role)],
        expires: DateTime.UtcNow.AddMinutes(15), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)), SecurityAlgorithms.HmacSha256))));

    [ReceivingSqlFact]
    public async Task ExcelPreviewWritesNothingAndImportIsAtomicRejectsRetriesAndIsAdminOnly()
    {
        await using var db = f.Context(); var cats = CropWorkbookTests.Categories();
        foreach (var category in cats) category.CategoryId = 0;
        cats[1].ParentId = null; cats[1].Parent = cats[0];
        db.Categories.AddRange(cats); await db.SaveChangesAsync();
        using var api = await f.ApiAsync(); api.DefaultRequestHeaders.Authorization = Token("ADMINISTRATOR");
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/crop-types/template")).StatusCode);
        var before = await db.CropTypes.CountAsync();
        async Task<HttpResponseMessage> Send(string endpoint, byte[] bytes)
        {
            using var content = new MultipartFormDataContent(); content.Add(new ByteArrayContent(bytes), "file", "crops.xlsx");
            return await api.PostAsync("/api/crop-types/" + endpoint, content);
        }
        var valid = CropWorkbookTests.Workbook(cats, ["LT", "NGO", "ngô mới một", "BULK_A", "TRUE"], ["LT", "NGO", "ngô mới hai", "BULK_B", "TRUE"]);
        using var preview = JsonDocument.Parse(await (await Send("import-preview", valid)).Content.ReadAsStringAsync());
        Assert.Empty(preview.RootElement.GetProperty("data").GetProperty("errors").EnumerateArray()); Assert.Equal(before, await db.CropTypes.CountAsync());
        var invalid = CropWorkbookTests.Workbook(cats, ["LT", "NGO", "Loại hợp lệ", "VALID", "TRUE"], ["WRONG", "NGO", "Loại lỗi", "INVALID", "TRUE"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send("import", invalid)).StatusCode); Assert.Equal(before, await db.CropTypes.CountAsync());
        Assert.Equal(HttpStatusCode.OK, (await Send("import", valid)).StatusCode); Assert.Equal(before + 2, await db.CropTypes.CountAsync());
        Assert.Equal("Ngô mới một", await db.CropTypes.Where(x => x.CropCode == "BULK_A").Select(x => x.CropName).SingleAsync());
        var accentLookup = await api.GetStringAsync("/api/crop-types/lookup?search=ngo");
        Assert.Contains("BULK_A", accentLookup);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send("import", valid)).StatusCode); Assert.Equal(before + 2, await db.CropTypes.CountAsync());
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_CROP_IMPORT_TEST_FAILURE ON dbo.CROP_TYPE AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE CropCode='ROLLBACK_B') THROW 51000, 'Deliberate isolated import failure', 1; END");
        var lateFailure = CropWorkbookTests.Workbook(cats, ["LT", "NGO", "Rollback một", "ROLLBACK_A", "TRUE"], ["LT", "NGO", "Rollback hai", "ROLLBACK_B", "TRUE"]);
        Assert.False((await Send("import", lateFailure)).IsSuccessStatusCode);
        Assert.Equal(before + 2, await db.CropTypes.CountAsync());
        Assert.False(await db.CropTypes.AnyAsync(x => x.CropCode == "ROLLBACK_A"));
        api.DefaultRequestHeaders.Authorization = Token("SUPPLIER");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("import", valid)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/api/crop-types/template")).StatusCode);
    }
}
