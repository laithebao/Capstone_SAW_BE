using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SAW.Infrastructure.Persistence;

#nullable disable

namespace SAW.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261023150000_MakeSupplyContractQualityGradeOptional")]
public partial class MakeSupplyContractQualityGradeOptional : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "QualityGrade",
            table: "SUPPLY_CONTRACT",
            type: "nvarchar(5)",
            maxLength: 5,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "nvarchar(5)",
            oldMaxLength: 5);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE dbo.SUPPLY_CONTRACT SET QualityGrade = 'A' WHERE QualityGrade IS NULL;");
        migrationBuilder.AlterColumn<string>(
            name: "QualityGrade",
            table: "SUPPLY_CONTRACT",
            type: "nvarchar(5)",
            maxLength: 5,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(5)",
            oldMaxLength: 5,
            oldNullable: true);
    }
}
