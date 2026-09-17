using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketFactors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "market_cash_flows",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BuyValueCrore = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SellValueCrore = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    NetValueCrore = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Source = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    FetchedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_cash_flows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "market_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    TimeIst = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    Region = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Importance = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Source = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "market_futures_daily",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Underlying = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    InstrumentKind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Close = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PreviousClose = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SettlementPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UnderlyingPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    OpenInterest = table.Column<long>(type: "bigint", nullable: false),
                    OpenInterestChange = table.Column<long>(type: "bigint", nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    TurnoverValue = table.Column<decimal>(type: "numeric(22,2)", precision: 22, scale: 2, nullable: false),
                    Source = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    FetchedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_futures_daily", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "market_participant_oi",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ClientType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FutureIndexLong = table.Column<long>(type: "bigint", nullable: false),
                    FutureIndexShort = table.Column<long>(type: "bigint", nullable: false),
                    FutureStockLong = table.Column<long>(type: "bigint", nullable: false),
                    FutureStockShort = table.Column<long>(type: "bigint", nullable: false),
                    OptionIndexCallLong = table.Column<long>(type: "bigint", nullable: false),
                    OptionIndexPutLong = table.Column<long>(type: "bigint", nullable: false),
                    OptionIndexCallShort = table.Column<long>(type: "bigint", nullable: false),
                    OptionIndexPutShort = table.Column<long>(type: "bigint", nullable: false),
                    OptionStockCallLong = table.Column<long>(type: "bigint", nullable: false),
                    OptionStockPutLong = table.Column<long>(type: "bigint", nullable: false),
                    OptionStockCallShort = table.Column<long>(type: "bigint", nullable: false),
                    OptionStockPutShort = table.Column<long>(type: "bigint", nullable: false),
                    TotalLong = table.Column<long>(type: "bigint", nullable: false),
                    TotalShort = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    FetchedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_participant_oi", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_market_cash_flows_Date_Category",
                table: "market_cash_flows",
                columns: new[] { "Date", "Category" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_market_events_Date",
                table: "market_events",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_market_events_Date_Category_Title",
                table: "market_events",
                columns: new[] { "Date", "Category", "Title" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_market_futures_daily_Date_Underlying_ExpiryDate",
                table: "market_futures_daily",
                columns: new[] { "Date", "Underlying", "ExpiryDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_market_futures_daily_Underlying_Date",
                table: "market_futures_daily",
                columns: new[] { "Underlying", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_market_participant_oi_Date_ClientType",
                table: "market_participant_oi",
                columns: new[] { "Date", "ClientType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "market_cash_flows");

            migrationBuilder.DropTable(
                name: "market_events");

            migrationBuilder.DropTable(
                name: "market_futures_daily");

            migrationBuilder.DropTable(
                name: "market_participant_oi");
        }
    }
}
