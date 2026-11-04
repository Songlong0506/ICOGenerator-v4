using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class ThuModelChoPoc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PocVariants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModelLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Output = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModelCalls = table.Column<int>(type: "int", nullable: false),
                    PromptTokens = table.Column<long>(type: "bigint", nullable: false),
                    CompletionTokens = table.Column<long>(type: "bigint", nullable: false),
                    CostUsd = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PocVariants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PocVariants_AiModels_AiModelId",
                        column: x => x.AiModelId,
                        principalTable: "AiModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PocVariants_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PocVariants_AiModelId",
                table: "PocVariants",
                column: "AiModelId");

            migrationBuilder.CreateIndex(
                name: "IX_PocVariants_ProjectId_CreatedAt",
                table: "PocVariants",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PocVariants_Status_CreatedAt",
                table: "PocVariants",
                columns: new[] { "Status", "CreatedAt" });

            // Quyền mới PocModelExperiment cho vai trò Admin trên DB ĐÃ CÓ dữ liệu: DbInitializer chỉ seed
            // quyền mặc định khi bảng RolePermissions còn trống, nên không có dòng này thì Admin của hệ thống
            // đang chạy không thấy nút "Thử model khác" cho tới khi tự tick ở màn Roles. Bảng trống (DB mới)
            // thì bỏ qua — seed sẽ cấp toàn bộ quyền cho Admin như thường.
            migrationBuilder.Sql(@"
INSERT INTO RolePermissions (Id, Role, Permission)
SELECT NEWID(), 'Admin', 'PocModelExperiment'
WHERE EXISTS (SELECT 1 FROM RolePermissions)
  AND NOT EXISTS (SELECT 1 FROM RolePermissions WHERE Role = 'Admin' AND Permission = 'PocModelExperiment');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM RolePermissions WHERE Permission = 'PocModelExperiment';");

            migrationBuilder.DropTable(
                name: "PocVariants");
        }
    }
}
