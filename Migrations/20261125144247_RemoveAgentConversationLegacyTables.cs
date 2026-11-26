using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAgentConversationLegacyTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FlowDiagram",
                table: "AgentConversations");

            migrationBuilder.DropColumn(
                name: "PermissionMatrix",
                table: "AgentConversations");

            migrationBuilder.DropColumn(
                name: "ReportMap",
                table: "AgentConversations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FlowDiagram",
                table: "AgentConversations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PermissionMatrix",
                table: "AgentConversations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportMap",
                table: "AgentConversations",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
