using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ActivityLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "activity_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    UserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Module = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Path = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    TargetType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    TargetId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activity_log", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_activity_log_Module_OccurredUtc",
                table: "activity_log",
                columns: new[] { "Module", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_activity_log_OccurredUtc",
                table: "activity_log",
                column: "OccurredUtc");

            migrationBuilder.CreateIndex(
                name: "IX_activity_log_UserId_OccurredUtc",
                table: "activity_log",
                columns: new[] { "UserId", "OccurredUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity_log");
        }
    }
}
