using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class DepartmentChecklistBucket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentChecklistItems_AgentId_DomainKey_Status",
                table: "AgentChecklistItems");

            migrationBuilder.DropColumn(
                name: "DomainKey",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DomainKey",
                table: "AgentChecklistItems");

            migrationBuilder.AddColumn<string>(
                name: "DepartmentCode",
                table: "AgentChecklistItems",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentChecklistItems_AgentId_DepartmentCode_Status",
                table: "AgentChecklistItems",
                columns: new[] { "AgentId", "DepartmentCode", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentChecklistItems_AgentId_DepartmentCode_Status",
                table: "AgentChecklistItems");

            migrationBuilder.DropColumn(
                name: "DepartmentCode",
                table: "AgentChecklistItems");

            migrationBuilder.AddColumn<string>(
                name: "DomainKey",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DomainKey",
                table: "AgentChecklistItems",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentChecklistItems_AgentId_DomainKey_Status",
                table: "AgentChecklistItems",
                columns: new[] { "AgentId", "DomainKey", "Status" });
        }
    }
}
