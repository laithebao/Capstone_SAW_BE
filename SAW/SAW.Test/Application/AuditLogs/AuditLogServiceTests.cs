using System.Text;
using Moq;
using SAW.Application.Exceptions;
using SAW.Application.Features.AuditLogs;
using SAW.Domain.Entities;

namespace SAW.Test.Application.AuditLogs;

public sealed class AuditLogServiceTests
{
    private readonly Mock<IAuditLogRepository> repository = new();
    private AuditLogService Service => new(repository.Object);
    private static AuditLogQuery Query => new("search", null, null, null, null, null, null, null, false, 0, 1000);

    [Theory]
    [InlineData("LOGIN_FAIL", "FAILED")][InlineData("ERROR", "FAILED")][InlineData("WARN", "WARNING")][InlineData("UPDATE", "SUCCESS")]
    public async Task Search_BoundsPagination_MapsStatusAndSystemActor_AndDetectsChanges(string action, string expected)
    {
        repository.Setup(x => x.SearchAsync(It.Is<AuditLogQuery>(q => q.Page == 1 && q.PageSize == 100 && q.Search == "search"), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new[] { new AuditLog { AuditLogId = 5, ActionType = action, EntityName = "ACCOUNT", OldDataJson = "{}" } }, 9));
        var result = await Service.SearchAsync(Query, default);
        var item = Assert.Single(result.Items);
        Assert.Equal(1, result.Page);
        Assert.Equal(100, result.PageSize);
        Assert.Equal(9, result.TotalCount);
        Assert.Equal("Hệ thống", item.ActorName);
        Assert.Equal(expected, item.Status);
        Assert.True(item.HasChanges);
    }

    [Fact]
    public async Task Detail_MapsActorAndChangeEvidence_AndUnknownLogReturnsNotFound()
    {
        repository.Setup(x => x.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(new AuditLog
        {
            AuditLogId = 5, ActionType = "UPDATE", EntityName = "ACCOUNT", OldDataJson = "{\"name\":\"old\"}", NewDataJson = "{\"name\":\"new\"}",
            Account = new Account { FullName = "Actor", Username = "actor", Email = "actor@saw.local", Role = new Role { RoleName = "Admin" } }
        });
        var result = await Service.GetAsync(5, default);
        Assert.Equal("Actor", result.ActorName);
        Assert.Equal("actor", result.ActorUsername);
        Assert.Equal("actor@saw.local", result.ActorEmail);
        Assert.Equal("Admin", result.ActorRole);
        Assert.Equal("{\"name\":\"old\"}", result.OldDataJson);
        Assert.Equal("{\"name\":\"new\"}", result.NewDataJson);
        await Assert.ThrowsAsync<NotFoundException>(() => Service.GetAsync(99, default));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"bad\")")][InlineData("+123")][InlineData("-123")][InlineData("@SUM(A1)")]
    public async Task Export_SanitizesSpreadsheetFormulasAndLineBreaks_AndExportsWithoutPagination(string description)
    {
        repository.Setup(x => x.SearchAsync(Query, false, It.IsAny<CancellationToken>())).ReturnsAsync((new[]
        {
            new AuditLog { AuditLogId = 5, ActionType = "UPDATE", EntityName = "ACCOUNT", Description = description + "\tline\r\nbreak", CreatedAt = new(2026, 11, 11, 1, 2, 3) }
        }, 1));
        var bytes = await Service.ExportCsvAsync(Query, default);
        Assert.Equal(Encoding.Unicode.GetPreamble(), bytes.Take(2));
        var text = Encoding.Unicode.GetString(bytes);
        Assert.Contains("ALOG-000005", text);
        Assert.Contains("2026-11-11 01:02:03", text);
        Assert.Contains("'" + description + " line  break", text);
        Assert.Equal(10, text.Split('\n')[1].TrimEnd('\r').Split('\t').Length);
        repository.Verify(x => x.SearchAsync(Query, false, It.IsAny<CancellationToken>()), Times.Once);
    }
}
