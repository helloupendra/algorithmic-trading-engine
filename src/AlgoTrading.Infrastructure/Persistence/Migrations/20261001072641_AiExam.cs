using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiExam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ai_reports_SubjectType",
                table: "ai_reports");

            migrationBuilder.CreateTable(
                name: "ai_exam_questions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Template = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: true),
                    Subject = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: ""),
                    Set = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Expected = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Number = table.Column<double>(type: "double precision", nullable: true),
                    Tolerance = table.Column<double>(type: "double precision", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RetiredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_exam_questions", x => x.Id);
                    table.CheckConstraint("CK_ai_exam_questions_Set", "\"Set\" IN ('practice', 'holdout')");
                });

            migrationBuilder.CreateTable(
                name: "ai_exams",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Trigger = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Repeats = table.Column<int>(type: "integer", nullable: false),
                    QuestionIdsJson = table.Column<string>(type: "text", nullable: false),
                    ReportId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_exams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ai_exam_answers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExamId = table.Column<long>(type: "bigint", nullable: false),
                    QuestionId = table.Column<long>(type: "bigint", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CallId = table.Column<long>(type: "bigint", nullable: true),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: ""),
                    Seconds = table.Column<double>(type: "double precision", nullable: false),
                    Answer = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false, defaultValue: ""),
                    Truth = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: ""),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_exam_answers", x => x.Id);
                    table.CheckConstraint("CK_ai_exam_answers_Outcome", "\"Outcome\" IN ('right', 'wrong', 'no-answer', 'moved')");
                    table.ForeignKey(
                        name: "FK_ai_exam_answers_ai_exam_questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "ai_exam_questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ai_exam_answers_ai_exams_ExamId",
                        column: x => x.ExamId,
                        principalTable: "ai_exams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ai_reports_SubjectType",
                table: "ai_reports",
                sql: "\"SubjectType\" IN ('run', 'news', 'filing', 'incident', 'check', 'exam')");

            migrationBuilder.CreateIndex(
                name: "IX_ai_exam_answers_Exam_Question_Attempt",
                table: "ai_exam_answers",
                columns: new[] { "ExamId", "QuestionId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_exam_answers_QuestionId",
                table: "ai_exam_answers",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_exam_questions_Day_Template",
                table: "ai_exam_questions",
                columns: new[] { "Day", "Template" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_exams_StartedUtc",
                table: "ai_exams",
                column: "StartedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_exam_answers");

            migrationBuilder.DropTable(
                name: "ai_exam_questions");

            migrationBuilder.DropTable(
                name: "ai_exams");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ai_reports_SubjectType",
                table: "ai_reports");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ai_reports_SubjectType",
                table: "ai_reports",
                sql: "\"SubjectType\" IN ('run', 'news', 'filing', 'incident', 'check')");
        }
    }
}
