using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveQuoteGreeks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Delta",
                table: "live_quotes_latest",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Gamma",
                table: "live_quotes_latest",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ImpliedVolatility",
                table: "live_quotes_latest",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OpenInterest",
                table: "live_quotes_latest",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Theta",
                table: "live_quotes_latest",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Vega",
                table: "live_quotes_latest",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Delta",
                table: "live_quotes_latest");

            migrationBuilder.DropColumn(
                name: "Gamma",
                table: "live_quotes_latest");

            migrationBuilder.DropColumn(
                name: "ImpliedVolatility",
                table: "live_quotes_latest");

            migrationBuilder.DropColumn(
                name: "OpenInterest",
                table: "live_quotes_latest");

            migrationBuilder.DropColumn(
                name: "Theta",
                table: "live_quotes_latest");

            migrationBuilder.DropColumn(
                name: "Vega",
                table: "live_quotes_latest");
        }
    }
}
