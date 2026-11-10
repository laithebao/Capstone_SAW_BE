using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SAW.Infrastructure.Persistence;

namespace SAW.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261108090000_MakeLegacySpoilageRiskScoreOptional")]
public sealed class MakeLegacySpoilageRiskScoreOptional : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        ChangeScoreNullability(migrationBuilder, "NULL");

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE dbo.AI_RISK_FORECAST SET SpoilageRiskScore = 0 WHERE SpoilageRiskScore IS NULL;");
        ChangeScoreNullability(migrationBuilder, "NOT NULL");
    }

    private static void ChangeScoreNullability(MigrationBuilder migrationBuilder, string nullability)
    {
        migrationBuilder.Sql($"""
            DECLARE @hadScoreCheck bit = CASE WHEN OBJECT_ID(N'dbo.CK_AI_RISK_FORECAST_Score', N'C') IS NULL THEN 0 ELSE 1 END;
            IF @hadScoreCheck = 1
                ALTER TABLE dbo.AI_RISK_FORECAST DROP CONSTRAINT CK_AI_RISK_FORECAST_Score;

            ALTER TABLE dbo.AI_RISK_FORECAST ALTER COLUMN SpoilageRiskScore decimal(6,2) {nullability};

            IF @hadScoreCheck = 1
                ALTER TABLE dbo.AI_RISK_FORECAST ADD CONSTRAINT CK_AI_RISK_FORECAST_Score CHECK (SpoilageRiskScore BETWEEN 0 AND 100);
            """);
    }
}
