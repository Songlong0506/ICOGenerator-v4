using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAiModelIsDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Bỏ cơ chế model mặc định: việc gán AI model cho agent giờ phải làm thủ công.
            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "AiModels");

            // Trước khi đặt AiModelId thành bắt buộc, gán model đầu tiên cho các agent
            // còn đang để trống để cột không vi phạm ràng buộc NOT NULL.
            migrationBuilder.Sql(
                "UPDATE Agents SET AiModelId = (SELECT TOP 1 Id FROM AiModels ORDER BY Name) " +
                "WHERE AiModelId IS NULL;");

            migrationBuilder.AlterColumn<Guid>(
                name: "AiModelId",
                table: "Agents",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "AiModelId",
                table: "Agents",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "AiModels",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
