using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PortfolioUserCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_portfolio_connections_app_user_UserId",
                table: "portfolio_connections",
                column: "UserId",
                principalTable: "app_user",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_portfolio_snapshots_app_user_UserId",
                table: "portfolio_snapshots",
                column: "UserId",
                principalTable: "app_user",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_portfolio_connections_app_user_UserId",
                table: "portfolio_connections");

            migrationBuilder.DropForeignKey(
                name: "FK_portfolio_snapshots_app_user_UserId",
                table: "portfolio_snapshots");
        }
    }
}
