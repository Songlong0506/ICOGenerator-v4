using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <summary>
    /// RolePermission.Role chuyển từ tên enum UserRole ("Admin"/"TeamDev"/"User") sang tên role IdentityServer
    /// đã chuẩn hoá — cột ma trận quyền giờ lấy từ IS4. Nới cột 50 → 200 rồi đổi dữ liệu cũ sang role IS4 tương
    /// ứng ([SsoRoleClaim] tại thời điểm viết migration; chuỗi cứng có chủ đích, không tham chiếu code đang sống).
    /// </summary>
    public partial class DoiCotRoleThanhTenRoleIdentityServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Role",
                table: "RolePermissions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);

            migrationBuilder.Sql("UPDATE RolePermissions SET Role = 'HCP_CBO_API.CBO.ADMIN' WHERE Role = 'Admin'");
            migrationBuilder.Sql("UPDATE RolePermissions SET Role = 'HCP_CBO_API.CBO.TEAMDEV' WHERE Role = 'TeamDev'");
            migrationBuilder.Sql("UPDATE RolePermissions SET Role = 'HCP_CBO_API.CBO.USER' WHERE Role = 'User'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Role IS4 không có giá trị enum tương ứng thì không quay về được ⇒ xoá (cột cũ chỉ nhận tên enum).
            migrationBuilder.Sql("UPDATE RolePermissions SET Role = 'Admin' WHERE Role = 'HCP_CBO_API.CBO.ADMIN'");
            migrationBuilder.Sql("UPDATE RolePermissions SET Role = 'TeamDev' WHERE Role = 'HCP_CBO_API.CBO.TEAMDEV'");
            migrationBuilder.Sql("UPDATE RolePermissions SET Role = 'User' WHERE Role = 'HCP_CBO_API.CBO.USER'");
            migrationBuilder.Sql("DELETE FROM RolePermissions WHERE Role NOT IN ('Admin', 'TeamDev', 'User')");

            migrationBuilder.AlterColumn<string>(
                name: "Role",
                table: "RolePermissions",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);
        }
    }
}
