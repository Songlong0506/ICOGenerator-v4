using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class InterviewTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EntityMap",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FlowMap",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScreenScopeMap",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntityMap",
                table: "AgentConversations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FlowMap",
                table: "AgentConversations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScreenScopeMap",
                table: "AgentConversations",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EntityMap",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "FlowMap",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ScreenScopeMap",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "EntityMap",
                table: "AgentConversations");

            migrationBuilder.DropColumn(
                name: "FlowMap",
                table: "AgentConversations");

            migrationBuilder.DropColumn(
                name: "ScreenScopeMap",
                table: "AgentConversations");
        }
    }
}
