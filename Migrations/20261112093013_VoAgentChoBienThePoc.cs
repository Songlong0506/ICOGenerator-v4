using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class VoAgentChoBienThePoc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Harness",
                table: "PocVariants",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                // Mọi biến thể đã có đều dựng bằng đường của app.
                defaultValue: "Builtin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Harness",
                table: "PocVariants");
        }
    }
}
