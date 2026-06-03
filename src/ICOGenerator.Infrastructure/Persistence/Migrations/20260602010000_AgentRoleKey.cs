using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class AgentRoleKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RoleKey",
                table: "Agents",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "General");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_RoleKey",
                table: "Agents",
                column: "RoleKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Agents_RoleKey",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "RoleKey",
                table: "Agents");
        }
    }
}
