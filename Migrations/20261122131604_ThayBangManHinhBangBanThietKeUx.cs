using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class ThayBangManHinhBangBanThietKeUx : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScreenScopeMap",
                table: "AgentConversations");

            migrationBuilder.RenameColumn(
                name: "ScreenScopeMap",
                table: "Projects",
                newName: "UxBlueprint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "UxBlueprint",
                table: "Projects",
                newName: "ScreenScopeMap");

            migrationBuilder.AddColumn<string>(
                name: "ScreenScopeMap",
                table: "AgentConversations",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
