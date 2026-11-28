using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SAW.API.Serialization;
using SAW.Domain.Common;
using SAW.Infrastructure.Migrations;

namespace SAW.Test.Application;

public sealed class VietnamTimeTests
{
    [Theory]
    [InlineData("2026-11-26T16:59:59Z", "2026-11-26")]
    [InlineData("2026-11-26T17:00:00Z", "2026-11-27")]
    [InlineData("2026-12-31T17:00:00Z", "2027-01-01")]
    public void VietnamBusinessDayUsesVietnamMidnight(string utc, string expected)
    {
        var instant = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(DateOnly.Parse(expected), VietnamTime.DateOf(instant));
        var start = VietnamTime.StartOfDayUtc(DateOnly.Parse(expected));
        Assert.Equal(DateTimeKind.Utc, start.Kind); Assert.Equal(17, start.Hour);
        Assert.Equal(DateOnly.Parse(expected).AddDays(-1), DateOnly.FromDateTime(start));
    }

    [Fact]
    public void WireTimestampsAreExplicitUtcAndCalendarDatesRemainUnchanged()
    {
        var options = new JsonSerializerOptions(); options.Converters.Add(new UtcDateTimeJsonConverter());
        var sqlValue = new DateTime(2026, 11, 26, 17, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal("\"2026-11-26T17:00:00Z\"", JsonSerializer.Serialize(sqlValue, options));
        var aware = JsonSerializer.Deserialize<DateTime>("\"2026-11-27T00:00:00+07:00\"", options);
        Assert.Equal(DateTime.SpecifyKind(sqlValue, DateTimeKind.Utc), aware.ToUniversalTime());
        var legacyQc = JsonSerializer.Deserialize<DateTime>("\"2026-11-27T00:00:00\"", options);
        Assert.Equal(DateTimeKind.Unspecified, legacyQc.Kind);
        Assert.Equal("\"2026-11-27\"", JsonSerializer.Serialize(new DateOnly(2026, 11, 27), options));
        Assert.Equal(DateTime.SpecifyKind(sqlValue, DateTimeKind.Utc), VietnamTime.QueryUtc(legacyQc));
    }

    [Fact]
    public void MigrationOnlyChangesClockDefaultsAndGuardedTriggerMetadata()
    {
        var migration = new NormalizeUtcTimestampDefaults();
        Assert.Empty(migration.UpOperations.OfType<AlterColumnOperation>());
        var defaults = migration.UpOperations.OfType<SqlOperation>().Where(x => x.Sql.Contains("DROP CONSTRAINT")).ToArray();
        Assert.Equal(34, defaults.Length);
        Assert.Equal(2, defaults.Count(x => x.Sql.Contains("ADD DEFAULT (CONVERT(date")));
        Assert.All(defaults, x => { Assert.Contains("SYSUTCDATETIME()", x.Sql); Assert.DoesNotContain("ALTER COLUMN", x.Sql); });
        Assert.Equal(35, migration.UpOperations.Count);
        Assert.DoesNotContain(migration.UpOperations, x => x is UpdateDataOperation or InsertDataOperation or DeleteDataOperation or DropTableOperation);
    }
}
