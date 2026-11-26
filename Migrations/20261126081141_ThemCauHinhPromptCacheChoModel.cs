using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class ThemCauHinhPromptCacheChoModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DisablePromptCache",
                table: "AiModels",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "StickySessionRouting",
                table: "AiModels",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisablePromptCache",
                table: "AiModels");

            migrationBuilder.DropColumn(
                name: "StickySessionRouting",
                table: "AiModels");
        }
    }
}
