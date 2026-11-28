using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAW.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDistributorAiChatHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AI_CHAT_TURN",
                columns: table => new
                {
                    AIChatTurnID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccountID = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Reply = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BatchContextId = table.Column<long>(type: "bigint", nullable: true),
                    BatchContextCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RepliedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AI_CHAT_TURN", x => x.AIChatTurnID);
                    table.ForeignKey(
                        name: "FK_AI_CHAT_TURN_ACCOUNT_AccountID",
                        column: x => x.AccountID,
                        principalTable: "ACCOUNT",
                        principalColumn: "AccountID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AI_CHAT_TURN_AccountID_AIChatTurnID",
                table: "AI_CHAT_TURN",
                columns: new[] { "AccountID", "AIChatTurnID" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AI_CHAT_TURN");
        }
    }
}
