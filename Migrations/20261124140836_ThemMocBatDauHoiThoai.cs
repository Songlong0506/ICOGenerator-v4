using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class ThemMocBatDauHoiThoai : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ConversationStartedAtUtc",
                table: "Projects",
                type: "datetime2",
                nullable: true);

            // Dự án đã bấm New Chat trước khi có cột: lấy mốc lưu trữ muộn nhất của hội thoại làm mốc,
            // để các run của hội thoại cũ cũng được ẩn khỏi timeline ngay sau khi nâng cấp.
            migrationBuilder.Sql(@"
UPDATE p SET p.ConversationStartedAtUtc = c.LastArchivedAt
FROM Projects p
JOIN (SELECT ProjectId, MAX(ArchivedAt) AS LastArchivedAt
      FROM AgentConversations
      WHERE ArchivedAt IS NOT NULL
      GROUP BY ProjectId) c ON c.ProjectId = p.Id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConversationStartedAtUtc",
                table: "Projects");
        }
    }
}
