using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptTemplateVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PromptVersionId",
                table: "EvalResults",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromptVersionNumber",
                table: "EvalResults",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PromptTemplateVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PromptKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChangeNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptTemplateVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PromptTemplateVersions_PromptKey_IsActive",
                table: "PromptTemplateVersions",
                columns: new[] { "PromptKey", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_PromptTemplateVersions_PromptKey_VersionNumber",
                table: "PromptTemplateVersions",
                columns: new[] { "PromptKey", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PromptTemplateVersions");

            migrationBuilder.DropColumn(
                name: "PromptVersionId",
                table: "EvalResults");

            migrationBuilder.DropColumn(
                name: "PromptVersionNumber",
                table: "EvalResults");
        }
    }
}
