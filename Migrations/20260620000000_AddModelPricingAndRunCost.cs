using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class AddModelPricingAndRunCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Đơn giá token để trang Usage quy ra tiền ($). Mặc định 0 → model cũ/tự host không phát sinh chi phí
            // cho tới khi người dùng nhập giá. decimal(18,6) đủ cho đơn giá lẻ kiểu $0.075 / 1M token.
            migrationBuilder.AddColumn<decimal>(
                name: "InputPricePerMillionTokens",
                table: "AiModels",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OutputPricePerMillionTokens",
                table: "AiModels",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: 0m);

            // Gắn lời gọi model với WorkflowRun để tính chi phí "theo run". Nullable: log cũ và chat BA không có run.
            // Cố ý KHÔNG tạo FK (tránh multiple-cascade-path từ Projects); chỉ đánh index để gom nhóm báo cáo.
            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowRunId",
                table: "AgentModelCallLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentModelCallLogs_WorkflowRunId",
                table: "AgentModelCallLogs",
                column: "WorkflowRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentModelCallLogs_WorkflowRunId",
                table: "AgentModelCallLogs");

            migrationBuilder.DropColumn(
                name: "WorkflowRunId",
                table: "AgentModelCallLogs");

            migrationBuilder.DropColumn(
                name: "OutputPricePerMillionTokens",
                table: "AiModels");

            migrationBuilder.DropColumn(
                name: "InputPricePerMillionTokens",
                table: "AiModels");
        }
    }
}
