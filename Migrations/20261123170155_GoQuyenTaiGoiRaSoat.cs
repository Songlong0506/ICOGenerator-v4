using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class GoQuyenTaiGoiRaSoat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Gói rà soát (.zip) đã bị gỡ cùng giá trị enum AppPermission.RequirementsDownloadPackage. Cột
            // Permission lưu enum dạng chuỗi, nên dòng còn sót sẽ làm EF ném lỗi khi đọc bảng quyền — xoá hết.
            migrationBuilder.Sql("DELETE FROM RolePermissions WHERE Permission = 'RequirementsDownloadPackage';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Không khôi phục: giá trị enum không còn trong code nên không có gì để cấp lại.
        }
    }
}
