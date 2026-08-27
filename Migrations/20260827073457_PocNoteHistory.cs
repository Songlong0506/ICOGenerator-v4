using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class PocNoteHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BriefVersion",
                table: "PocComments",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "draft");

            migrationBuilder.AddColumn<string>(
                name: "Quote",
                table: "PocComments",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RevisionTaskId",
                table: "PocComments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Route",
                table: "PocComments",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Target",
                table: "PocComments",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                // Enum lưu dạng CHUỖI: để trống là dòng cũ không đọc lại được (parse lỗi lúc query).
                defaultValue: "Poc");

            migrationBuilder.AddColumn<DateTime>(
                name: "WithdrawnAtUtc",
                table: "PocComments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WithdrawnByUsername",
                table: "PocComments",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PocComments_ProjectId_BriefVersion_CreatedAt",
                table: "PocComments",
                columns: new[] { "ProjectId", "BriefVersion", "CreatedAt" });

            // ==== Nạp lại dữ liệu cũ ====
            // Ghi chú đã có trong DB đều là ghi chú POC (đường Brief chưa từng lưu dòng nào), và đường xử
            // lý của chúng suy được từ Status: đã gửi Dev (Sent/Addressed) hay đã gửi về Requirement.
            migrationBuilder.Sql(@"
                UPDATE PocComments
                SET Route = CASE
                        WHEN Status = 'RoutedToRequirement' THEN 'Requirement'
                        WHEN Status IN ('Sent', 'Addressed') THEN 'FixPoc'
                    END
                WHERE Status <> 'Open';");

            // Phiên bản Brief của ghi chú cũ: bản Product Brief ĐÃ DUYỆT gần nhất được tạo TRƯỚC lúc ghim
            // (không có mốc thời điểm duyệt trong ProjectDocuments, nên đây là xấp xỉ đúng nhất còn lại —
            // vẫn hơn hẳn để trống cả bảng lịch sử). Tên file lấy theo ProjectArtifactCatalog.ProductBrief.
            migrationBuilder.Sql(@"
                UPDATE c
                SET BriefVersion = COALESCE((
                    SELECT TOP 1 d.VersionName
                    FROM ProjectDocuments d
                    WHERE d.ProjectId = c.ProjectId
                      AND d.IsApproved = 1
                      AND d.FileName = 'ProductBrief.docx'
                      AND d.CreatedAt <= c.CreatedAt
                    ORDER BY d.CreatedAt DESC), 'draft')
                FROM PocComments c;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PocComments_ProjectId_BriefVersion_CreatedAt",
                table: "PocComments");

            migrationBuilder.DropColumn(
                name: "BriefVersion",
                table: "PocComments");

            migrationBuilder.DropColumn(
                name: "Quote",
                table: "PocComments");

            migrationBuilder.DropColumn(
                name: "RevisionTaskId",
                table: "PocComments");

            migrationBuilder.DropColumn(
                name: "Route",
                table: "PocComments");

            migrationBuilder.DropColumn(
                name: "Target",
                table: "PocComments");

            migrationBuilder.DropColumn(
                name: "WithdrawnAtUtc",
                table: "PocComments");

            migrationBuilder.DropColumn(
                name: "WithdrawnByUsername",
                table: "PocComments");
        }
    }
}
