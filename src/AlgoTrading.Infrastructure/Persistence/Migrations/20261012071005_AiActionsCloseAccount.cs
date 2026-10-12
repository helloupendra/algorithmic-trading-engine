using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiActionsCloseAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ai_actions_Kind",
                table: "ai_actions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ai_actions_Kind",
                table: "ai_actions",
                sql: "\"Kind\" IN ('stop_run', 'start_run', 'square_off', 'agent_switch', 'close_account')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ai_actions_Kind",
                table: "ai_actions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ai_actions_Kind",
                table: "ai_actions",
                sql: "\"Kind\" IN ('stop_run', 'start_run', 'square_off', 'agent_switch')");
        }
    }
}
