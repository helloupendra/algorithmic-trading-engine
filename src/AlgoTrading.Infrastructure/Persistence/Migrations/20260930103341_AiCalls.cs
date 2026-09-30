using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_calls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AgentKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Tier = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: ""),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: ""),
                    RequestedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: ""),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    ConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: ""),
                    ChainJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: ""),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Seconds = table.Column<double>(type: "double precision", nullable: false),
                    PromptTokens = table.Column<int>(type: "integer", nullable: true),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: true),
                    TotalTokens = table.Column<int>(type: "integer", nullable: true),
                    AttemptsJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    SystemPrompt = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    MessagesJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    Summary = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    Answer = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Reasoning = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    FinishReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: ""),
                    MaxTokens = table.Column<int>(type: "integer", nullable: false),
                    Temperature = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_calls", x => x.Id);
                    table.CheckConstraint("CK_ai_calls_Outcome", "\"Outcome\" IN ('running', 'ok', 'failed', 'refused', 'cancelled')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_calls_AgentKey_Id",
                table: "ai_calls",
                columns: new[] { "AgentKey", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_calls_CreatedUtc",
                table: "ai_calls",
                column: "CreatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_calls");
        }
    }
}
