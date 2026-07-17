using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class BudgetSpendIndexAndGateConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AgentModelCallLogs_CreatedAt",
                table: "AgentModelCallLogs",
                column: "CreatedAt")
                .Annotation("SqlServer:Include", new[] { "ProjectId", "ModelId", "PromptTokens", "CompletionTokens" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentModelCallLogs_CreatedAt",
                table: "AgentModelCallLogs");
        }
    }
}
