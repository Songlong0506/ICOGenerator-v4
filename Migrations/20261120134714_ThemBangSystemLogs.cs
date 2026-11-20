using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class ThemBangSystemLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SystemLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Level = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceContext = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Exception = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SystemLogs_Level_Timestamp",
                table: "SystemLogs",
                columns: new[] { "Level", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemLogs_Timestamp",
                table: "SystemLogs",
                column: "Timestamp");

            // Quyền mới SystemLogView cho Admin + TeamDev trên DB ĐÃ CÓ dữ liệu: DbInitializer chỉ seed quyền
            // mặc định khi bảng RolePermissions còn trống, nên không có đoạn này thì hệ thống đang chạy không
            // ai (trừ SuperAdmin) thấy màn System Logs cho tới khi admin tự tick. Bảng trống thì seed lo.
            migrationBuilder.Sql(@"
INSERT INTO RolePermissions (Id, Role, Permission)
SELECT NEWID(), r.Role, 'SystemLogView'
FROM (VALUES ('Admin'), ('TeamDev')) AS r(Role)
WHERE EXISTS (SELECT 1 FROM RolePermissions)
  AND NOT EXISTS (SELECT 1 FROM RolePermissions p WHERE p.Role = r.Role AND p.Permission = 'SystemLogView');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM RolePermissions WHERE Permission = 'SystemLogView';");

            migrationBuilder.DropTable(
                name: "SystemLogs");
        }
    }
}
