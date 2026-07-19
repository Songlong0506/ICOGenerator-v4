using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class AgentDropNameUseRoleKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Agents_RoleKey",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Agents");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_RoleKey",
                table: "Agents",
                column: "RoleKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Agents_RoleKey",
                table: "Agents");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Agents",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_RoleKey",
                table: "Agents",
                column: "RoleKey");
        }
    }
}
