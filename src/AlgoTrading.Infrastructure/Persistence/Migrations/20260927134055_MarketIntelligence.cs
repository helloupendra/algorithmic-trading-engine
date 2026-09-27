using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketIntelligence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "corporate_announcements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Exchange = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Company = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Subject = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Details = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    AttachmentUrl = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    AnnouncedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UniqueKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Sentiment = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: true),
                    Importance = table.Column<short>(type: "smallint", nullable: true),
                    Symbols = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Topics = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    ScoredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ScoreModel = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_corporate_announcements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "corporate_calendar",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Exchange = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Company = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Purpose = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    EventDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UniqueKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_corporate_calendar", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "market_breadth_daily",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Exchange = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Advances = table.Column<int>(type: "integer", nullable: false),
                    Declines = table.Column<int>(type: "integer", nullable: false),
                    Unchanged = table.Column<int>(type: "integer", nullable: false),
                    Traded = table.Column<int>(type: "integer", nullable: false),
                    TurnoverCr = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Highs52w = table.Column<int>(type: "integer", nullable: true),
                    Lows52w = table.Column<int>(type: "integer", nullable: true),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_breadth_daily", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "market_global_daily",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Open = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    High = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    Low = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    Close = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Volume = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: true),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FetchedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_global_daily", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "market_quote_snapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    PreviousClose = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    ChangePct = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    AsOfUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FetchedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_quote_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "news_items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Link = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    LinkHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Sentiment = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: true),
                    Importance = table.Column<short>(type: "smallint", nullable: true),
                    Symbols = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Topics = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    ScoredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ScoreModel = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_news_items", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_corporate_announcements_FirstSeenUtc",
                table: "corporate_announcements",
                column: "FirstSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_corporate_announcements_ScoredUtc",
                table: "corporate_announcements",
                column: "ScoredUtc");

            migrationBuilder.CreateIndex(
                name: "IX_corporate_announcements_Symbol_AnnouncedUtc",
                table: "corporate_announcements",
                columns: new[] { "Symbol", "AnnouncedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_corporate_announcements_UniqueKey",
                table: "corporate_announcements",
                column: "UniqueKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_corporate_calendar_EventDate_Symbol",
                table: "corporate_calendar",
                columns: new[] { "EventDate", "Symbol" });

            migrationBuilder.CreateIndex(
                name: "IX_corporate_calendar_UniqueKey",
                table: "corporate_calendar",
                column: "UniqueKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_market_breadth_daily_Exchange_Date",
                table: "market_breadth_daily",
                columns: new[] { "Exchange", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_market_global_daily_Symbol_Date",
                table: "market_global_daily",
                columns: new[] { "Symbol", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_market_quote_snapshots_Key_FetchedUtc",
                table: "market_quote_snapshots",
                columns: new[] { "Key", "FetchedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_news_items_Category_FirstSeenUtc",
                table: "news_items",
                columns: new[] { "Category", "FirstSeenUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_news_items_FirstSeenUtc",
                table: "news_items",
                column: "FirstSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_news_items_LinkHash",
                table: "news_items",
                column: "LinkHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_news_items_ScoredUtc",
                table: "news_items",
                column: "ScoredUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "corporate_announcements");

            migrationBuilder.DropTable(
                name: "corporate_calendar");

            migrationBuilder.DropTable(
                name: "market_breadth_daily");

            migrationBuilder.DropTable(
                name: "market_global_daily");

            migrationBuilder.DropTable(
                name: "market_quote_snapshots");

            migrationBuilder.DropTable(
                name: "news_items");
        }
    }
}
