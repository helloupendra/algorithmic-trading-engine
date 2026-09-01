using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOptionFieldsToInstrument : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OptionType",
                table: "instruments",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "StrikePrice",
                table: "instruments",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Underlying",
                table: "instruments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_instruments_ExpiryDate",
                table: "instruments",
                column: "ExpiryDate");

            migrationBuilder.CreateIndex(
                name: "IX_instruments_Underlying",
                table: "instruments",
                column: "Underlying");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_instruments_ExpiryDate",
                table: "instruments");

            migrationBuilder.DropIndex(
                name: "IX_instruments_Underlying",
                table: "instruments");

            migrationBuilder.DropColumn(
                name: "OptionType",
                table: "instruments");

            migrationBuilder.DropColumn(
                name: "StrikePrice",
                table: "instruments");

            migrationBuilder.DropColumn(
                name: "Underlying",
                table: "instruments");
        }
    }
}
