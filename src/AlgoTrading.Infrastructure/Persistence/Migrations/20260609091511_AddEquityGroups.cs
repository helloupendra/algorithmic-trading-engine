using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEquityGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "equity_groups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equity_groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "equity_group_members",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EquityGroupId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equity_group_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_equity_group_members_equity_groups_EquityGroupId",
                        column: x => x.EquityGroupId,
                        principalTable: "equity_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_equity_group_members_EquityGroupId_IsEnabled",
                table: "equity_group_members",
                columns: new[] { "EquityGroupId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_equity_group_members_EquityGroupId_Symbol_EffectiveFrom_Eff~",
                table: "equity_group_members",
                columns: new[] { "EquityGroupId", "Symbol", "EffectiveFrom", "EffectiveTo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_equity_group_members_Symbol",
                table: "equity_group_members",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_equity_groups_Exchange_IsEnabled",
                table: "equity_groups",
                columns: new[] { "Exchange", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_equity_groups_Name",
                table: "equity_groups",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "equity_group_members");

            migrationBuilder.DropTable(
                name: "equity_groups");
        }
    }
}
