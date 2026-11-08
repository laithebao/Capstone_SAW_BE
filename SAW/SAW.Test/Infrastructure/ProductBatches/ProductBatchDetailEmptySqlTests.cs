using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace SAW.Test.Infrastructure.ProductBatches;

// The base disposable fixture has no status trigger; represents a valid legacy batch with no history.
// Never delete an immutable history row or change the dev trigger to produce an empty state.
public sealed class ProductBatchDetailEmptySqlTests(ReceivingSqlFixture fixture):IClassFixture<ReceivingSqlFixture>
{
    [ReceivingSqlFact]
    public async Task Valid_batch_http_detail_returns_null_qc_and_empty_receipts_history_transactions_without_writes()
    {
        var batch=await fixture.BatchAsync();await using var db=fixture.Context();
        Assert.False(await db.BatchStatusHistories.AnyAsync(h=>h.ProductBatchId==batch.ProductBatchId));
        var audit=await db.AuditLogs.CountAsync();using var client=await fixture.ApiAsync();
        var token=new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("uc28-test","uc28-test",
            [new Claim("account_id",fixture.ActorId.ToString()),new Claim(ClaimTypes.Role,"OPERATION_STAFF")],expires:DateTime.UtcNow.AddMinutes(10),
            signingCredentials:new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ReceivingSqlFixture.JwtKey)),SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        using var response=await client.GetAsync($"/api/operation/product-batches/{batch.ProductBatchId}");Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var body=await response.Content.ReadFromJsonAsync<JsonElement>();var data=body.GetProperty("data");
        Assert.Equal(JsonValueKind.Null,data.GetProperty("latestQcInspection").ValueKind);
        foreach(var field in new[]{"goodsReceipts","statusHistory","warehouseTransactions"})Assert.Empty(data.GetProperty(field).EnumerateArray());
        Assert.True(data.GetProperty("canUpdateReceivingInformation").GetBoolean());
        Assert.Equal(audit,await db.AuditLogs.CountAsync());Assert.False(await db.BatchStatusHistories.AnyAsync(h=>h.ProductBatchId==batch.ProductBatchId));
        Assert.False(await db.QcInspections.AnyAsync(q=>q.ProductBatchId==batch.ProductBatchId));
        Assert.False(await db.GoodsReceipts.AnyAsync(r=>r.ProductBatchId==batch.ProductBatchId));
        Assert.False(await db.Inventories.AnyAsync(i=>i.ProductBatchId==batch.ProductBatchId));
    }
}
