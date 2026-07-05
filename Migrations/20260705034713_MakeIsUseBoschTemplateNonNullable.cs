using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class MakeIsUseBoschTemplateNonNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill các project cũ chưa chọn Generation Mode (NULL) về true (dùng Bosch template)
            // trước khi đổi cột sang NOT NULL — khớp mặc định mới của Project.IsUseBoschTemplate.
            migrationBuilder.Sql("UPDATE [Projects] SET [IsUseBoschTemplate] = 1 WHERE [IsUseBoschTemplate] IS NULL;");

            migrationBuilder.AlterColumn<bool>(
                name: "IsUseBoschTemplate",
                table: "Projects",
                type: "bit",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "IsUseBoschTemplate",
                table: "Projects",
                type: "bit",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "bit");
        }
    }
}
