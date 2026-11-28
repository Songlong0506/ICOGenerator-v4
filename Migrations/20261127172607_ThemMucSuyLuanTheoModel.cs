using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class ThemMucSuyLuanTheoModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent: một nhánh thử nghiệm cũ (MucSuyLuanTheoModel, chưa từng merge) đã tạo đúng cột này trên vài
            // DB dev mà không để lại dòng trong __EFMigrationsHistory. AddColumn trần ở đó là lỗi "column names must be
            // unique" ngay lúc app khởi động. Cùng kiểu, độ dài và mặc định "Default" như nhánh cũ.
            migrationBuilder.Sql("""
                IF COL_LENGTH('AiModels', 'ReasoningEffort') IS NULL
                    ALTER TABLE [AiModels] ADD [ReasoningEffort] nvarchar(20) NOT NULL
                        CONSTRAINT [DF_AiModels_ReasoningEffort] DEFAULT N'Default';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReasoningEffort",
                table: "AiModels");
        }
    }
}
