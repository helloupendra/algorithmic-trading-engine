using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProviderFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_broker_configs_BrokerName",
                table: "broker_configs");

            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                table: "market_ticks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                table: "live_ticks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                table: "live_quotes_latest",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                table: "live_bars",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                table: "candles",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            // Every row already in these tables came from FYERS — it is the only
            // connector this platform has ever had — so attribute them rather
            // than leaving a blank that reads as "unknown source".
            foreach (var table in new[] { "market_ticks", "live_ticks", "live_quotes_latest", "live_bars", "candles" })
            {
                migrationBuilder.Sql($"UPDATE \"{table}\" SET \"SourceKey\" = 'fyers' WHERE \"SourceKey\" = '';");
            }

            migrationBuilder.AddColumn<long>(
                name: "BrokerAccountId",
                table: "broker_sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BrokerAccountId",
                table: "broker_configs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "broker_accounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProviderKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broker_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "instrument_vendor_symbols",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProviderKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CanonicalSymbol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    VendorSymbol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InstrumentId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instrument_vendor_symbols", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "provider_bindings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Capability = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Segment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ProviderKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_bindings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_broker_configs_BrokerName_BrokerAccountId",
                table: "broker_configs",
                columns: new[] { "BrokerName", "BrokerAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_broker_configs_shared_broker",
                table: "broker_configs",
                column: "BrokerName",
                unique: true,
                filter: "\"BrokerAccountId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_broker_accounts_ProviderKey_UserId",
                table: "broker_accounts",
                columns: new[] { "ProviderKey", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_broker_accounts_shared_provider",
                table: "broker_accounts",
                column: "ProviderKey",
                unique: true,
                filter: "\"UserId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_broker_accounts_UserId",
                table: "broker_accounts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_instrument_vendor_symbols_ProviderKey_CanonicalSymbol",
                table: "instrument_vendor_symbols",
                columns: new[] { "ProviderKey", "CanonicalSymbol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_instrument_vendor_symbols_ProviderKey_VendorSymbol",
                table: "instrument_vendor_symbols",
                columns: new[] { "ProviderKey", "VendorSymbol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_bindings_Capability_Priority",
                table: "provider_bindings",
                columns: new[] { "Capability", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_bindings_Capability_Segment_ProviderKey",
                table: "provider_bindings",
                columns: new[] { "Capability", "Segment", "ProviderKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "broker_accounts");

            migrationBuilder.DropTable(
                name: "instrument_vendor_symbols");

            migrationBuilder.DropTable(
                name: "provider_bindings");

            migrationBuilder.DropIndex(
                name: "IX_broker_configs_BrokerName_BrokerAccountId",
                table: "broker_configs");

            migrationBuilder.DropIndex(
                name: "ix_broker_configs_shared_broker",
                table: "broker_configs");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                table: "market_ticks");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                table: "live_ticks");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                table: "live_quotes_latest");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                table: "live_bars");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                table: "candles");

            migrationBuilder.DropColumn(
                name: "BrokerAccountId",
                table: "broker_sessions");

            migrationBuilder.DropColumn(
                name: "BrokerAccountId",
                table: "broker_configs");

            migrationBuilder.CreateIndex(
                name: "IX_broker_configs_BrokerName",
                table: "broker_configs",
                column: "BrokerName",
                unique: true);
        }
    }
}
