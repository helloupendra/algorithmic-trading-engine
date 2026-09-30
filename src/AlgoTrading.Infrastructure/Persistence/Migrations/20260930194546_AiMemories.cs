using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiMemories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FeedbackBy",
                table: "ai_calls",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FeedbackNote",
                table: "ai_calls",
                type: "character varying(600)",
                maxLength: 600,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "FeedbackScore",
                table: "ai_calls",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FeedbackUtc",
                table: "ai_calls",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MemoryIdsJson",
                table: "ai_calls",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "ai_memories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AgentKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    Context = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false, defaultValue: ""),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Via = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "console"),
                    SourceCallId = table.Column<long>(type: "bigint", nullable: true),
                    SourceReportId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: ""),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: ""),
                    DecidedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActivatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetiredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Uses = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LastUsedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Ups = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Downs = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CheckPasses = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CheckFails = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Embedding = table.Column<float[]>(type: "real[]", nullable: false),
                    EmbeddingModel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_memories", x => x.Id);
                    table.CheckConstraint("CK_ai_memories_Kind", "\"Kind\" IN ('note', 'correction', 'lesson')");
                    table.CheckConstraint("CK_ai_memories_Source", "\"Source\" IN ('owner', 'feedback', 'check')");
                    table.CheckConstraint("CK_ai_memories_Status", "\"Status\" IN ('active', 'proposed', 'rejected', 'retired')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_memories_AgentKey_Status",
                table: "ai_memories",
                columns: new[] { "AgentKey", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_memories");

            migrationBuilder.DropColumn(
                name: "FeedbackBy",
                table: "ai_calls");

            migrationBuilder.DropColumn(
                name: "FeedbackNote",
                table: "ai_calls");

            migrationBuilder.DropColumn(
                name: "FeedbackScore",
                table: "ai_calls");

            migrationBuilder.DropColumn(
                name: "FeedbackUtc",
                table: "ai_calls");

            migrationBuilder.DropColumn(
                name: "MemoryIdsJson",
                table: "ai_calls");
        }
    }
}
