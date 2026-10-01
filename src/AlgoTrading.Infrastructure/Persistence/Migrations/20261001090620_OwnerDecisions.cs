using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OwnerDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "owner_decisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Decided = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    By = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: ""),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_owner_decisions", x => x.Id);
                    table.CheckConstraint("CK_owner_decisions_Status", "\"Status\" IN ('decided', 'default', 'open')");
                });

            migrationBuilder.CreateIndex(
                name: "UX_owner_decisions_Date_Title",
                table: "owner_decisions",
                columns: new[] { "Date", "Title" },
                unique: true);

            // The log lived in one system_settings value (varchar(2000)) until the sixth decision did not fit.
            // Its rows move here, oldest first so the ids keep the order; then the setting goes.
            migrationBuilder.Sql("""
                INSERT INTO owner_decisions ("Date", "Title", "Decided", "By", "Status", "RecordedBy", "RecordedUtc")
                SELECT (e->>'date')::date, left(e->>'title', 200), left(e->>'decided', 1000), left(coalesce(e->>'by', ''), 100),
                       CASE WHEN e->>'status' IN ('decided', 'default', 'open') THEN e->>'status' ELSE 'open' END,
                       left(coalesce(s."UpdatedBy", ''), 100), s."UpdatedUtc"
                FROM system_settings s
                CROSS JOIN LATERAL jsonb_array_elements(s."Value"::jsonb) WITH ORDINALITY AS a(e, n)
                WHERE s."Key" = 'owner.decisions'
                ORDER BY a.n DESC
                ON CONFLICT ("Date", "Title") DO NOTHING;
                DELETE FROM system_settings WHERE "Key" = 'owner.decisions';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "owner_decisions");
        }
    }
}
