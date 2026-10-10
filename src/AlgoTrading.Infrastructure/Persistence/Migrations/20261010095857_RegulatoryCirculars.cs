using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RegulatoryCirculars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "regulatory_circulars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Number = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Department = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    PublishedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Url = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    DocumentUrl = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Excerpt = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Relevance = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_regulatory_circulars", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_regulatory_circulars_FirstSeenUtc",
                table: "regulatory_circulars",
                column: "FirstSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_regulatory_circulars_Key",
                table: "regulatory_circulars",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "regulatory_circulars");
        }
    }
}
