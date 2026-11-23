using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class ThemSuatModelDungChung : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModelSlotAssignments",
                columns: table => new
                {
                    Slot = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AiModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedByUsername = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelSlotAssignments", x => x.Slot);
                    table.ForeignKey(
                        name: "FK_ModelSlotAssignments_AiModels_AiModelId",
                        column: x => x.AiModelId,
                        principalTable: "AiModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModelSlotAssignments_AiModelId",
                table: "ModelSlotAssignments",
                column: "AiModelId");

            // Chuyển cấu hình cũ: model trọng tài của agent BA (cột Agents.JudgeAiModelId) thành suất Judge. Lấy
            // agent BA tạo SỚM NHẤT — cùng thứ tự với BAAgentResolver, agent mà InterviewJudge thực sự đọc.
            migrationBuilder.Sql(@"
INSERT INTO ModelSlotAssignments (Slot, AiModelId, UpdatedByUsername, UpdatedAt)
SELECT TOP 1 'Judge', a.JudgeAiModelId, NULL, SYSUTCDATETIME()
FROM Agents a
WHERE a.RoleKey = 'BusinessAnalyst' AND a.JudgeAiModelId IS NOT NULL
ORDER BY a.CreatedAt, a.Id;");

            migrationBuilder.DropForeignKey(
                name: "FK_Agents_AiModels_JudgeAiModelId",
                table: "Agents");

            migrationBuilder.DropIndex(
                name: "IX_Agents_JudgeAiModelId",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "JudgeAiModelId",
                table: "Agents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "JudgeAiModelId",
                table: "Agents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_JudgeAiModelId",
                table: "Agents",
                column: "JudgeAiModelId");

            migrationBuilder.AddForeignKey(
                name: "FK_Agents_AiModels_JudgeAiModelId",
                table: "Agents",
                column: "JudgeAiModelId",
                principalTable: "AiModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql(@"
UPDATE a SET a.JudgeAiModelId = s.AiModelId
FROM Agents a
JOIN ModelSlotAssignments s ON s.Slot = 'Judge'
WHERE a.Id = (SELECT TOP 1 b.Id FROM Agents b WHERE b.RoleKey = 'BusinessAnalyst' ORDER BY b.CreatedAt, b.Id);");

            migrationBuilder.DropTable(
                name: "ModelSlotAssignments");
        }
    }
}
