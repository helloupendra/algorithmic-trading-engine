using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BrokerSessionProviderKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderKey",
                table: "broker_sessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            // Every session ever saved here was a FYERS session — attribute them,
            // or the live token would look like it belongs to no connector and
            // the provider-scoped lookup would miss it.
            migrationBuilder.Sql(
                "UPDATE \"broker_sessions\" SET \"ProviderKey\" = 'fyers' WHERE \"ProviderKey\" = '';");

            migrationBuilder.CreateIndex(
                name: "IX_broker_sessions_ProviderKey_IsActive",
                table: "broker_sessions",
                columns: new[] { "ProviderKey", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_broker_sessions_ProviderKey_IsActive",
                table: "broker_sessions");

            migrationBuilder.DropColumn(
                name: "ProviderKey",
                table: "broker_sessions");
        }
    }
}
