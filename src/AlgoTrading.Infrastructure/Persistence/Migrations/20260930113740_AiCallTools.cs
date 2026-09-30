using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiCallTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Rounds",
                table: "ai_calls",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ToolCalls",
                table: "ai_calls",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ToolsJson",
                table: "ai_calls",
                type: "text",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Rounds",
                table: "ai_calls");

            migrationBuilder.DropColumn(
                name: "ToolCalls",
                table: "ai_calls");

            migrationBuilder.DropColumn(
                name: "ToolsJson",
                table: "ai_calls");
        }
    }
}
