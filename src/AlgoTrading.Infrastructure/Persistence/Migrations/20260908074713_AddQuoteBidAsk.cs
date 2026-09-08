using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuoteBidAsk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AskPrice",
                table: "live_quotes_latest",
                type: "numeric(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AskSize",
                table: "live_quotes_latest",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BidPrice",
                table: "live_quotes_latest",
                type: "numeric(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BidSize",
                table: "live_quotes_latest",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AskPrice",
                table: "live_quotes_latest");

            migrationBuilder.DropColumn(
                name: "AskSize",
                table: "live_quotes_latest");

            migrationBuilder.DropColumn(
                name: "BidPrice",
                table: "live_quotes_latest");

            migrationBuilder.DropColumn(
                name: "BidSize",
                table: "live_quotes_latest");
        }
    }
}
