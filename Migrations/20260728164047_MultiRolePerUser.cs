using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <summary>
    /// Một người dùng có NHIỀU vai trò: cột đơn trị AppUsers.Role chuyển thành bảng nối AppUserRoles.
    /// Lý do: quyền giữa các vai trò giao nhau chứ không lồng nhau, nên cách cũ (gộp claim SSO về vai trò
    /// "cao nhất") làm mất quyền mà chỉ vai trò kia mới có. Quyền hiệu lực nay là HỢP quyền của mọi vai trò.
    /// Dữ liệu cũ được chuyển nguyên vẹn: mỗi user thành đúng một dòng AppUserRoles TRƯỚC khi bỏ cột.
    /// </summary>
    public partial class MultiRolePerUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppUserRoles",
                columns: table => new
                {
                    AppUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserRoles", x => new { x.AppUserId, x.Role });
                    table.ForeignKey(
                        name: "FK_AppUserRoles_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppUserRoles_Role",
                table: "AppUserRoles",
                column: "Role");

            // Chuyển dữ liệu TRƯỚC khi bỏ cột: mỗi user giữ đúng vai trò đang có, không ai bị mất quyền hay
            // được nâng quyền lúc nâng cấp. Cột Role cũ lưu tên enum dạng chuỗi nên copy thẳng sang.
            migrationBuilder.Sql(@"
                INSERT INTO AppUserRoles (AppUserId, Role)
                SELECT Id, Role FROM AppUsers WHERE Role IS NOT NULL AND Role <> '';");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "AppUsers");

            // Actor của audit log nay có thể giữ nhiều vai trò ⇒ ghi thành chuỗi nối "Admin,TeamDev".
            migrationBuilder.AlterColumn<string>(
                name: "ActorRole",
                table: "AuditLogs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ActorRole",
                table: "AuditLogs",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "AppUsers",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "User");

            // Quay lại cột đơn trị thì buộc phải chọn MỘT vai trò: giữ vai trò đặc quyền cao nhất, đúng
            // ngữ nghĩa "highest wins" của bản cũ. Người giữ nhiều vai trò sẽ MẤT quyền của các vai trò còn
            // lại — mất mát không tránh được khi lùi migration này.
            migrationBuilder.Sql(@"
                UPDATE u SET u.Role = r.Role
                FROM AppUsers u
                CROSS APPLY (
                    SELECT TOP 1 ur.Role
                    FROM AppUserRoles ur
                    WHERE ur.AppUserId = u.Id
                    ORDER BY CASE ur.Role
                        WHEN 'SuperAdmin' THEN 0
                        WHEN 'Admin' THEN 1
                        WHEN 'TeamDev' THEN 2
                        ELSE 3 END
                ) r;");

            migrationBuilder.DropTable(
                name: "AppUserRoles");
        }
    }
}
