using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class TranOutputTheoModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxOutputTokens",
                table: "AiModels",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxOutputTokens",
                table: "AiModels");
        }
    }
}
