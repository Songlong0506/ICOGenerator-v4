using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Lưu JSON các đáp án gợi ý kèm theo một lượt hỏi của BA (để UI render "chip" chọn nhanh).
            // Nullable: phần lớn lượt (tin người dùng, tóm tắt, lỗi) không có gợi ý.
            migrationBuilder.AddColumn<string>(
                name: "Suggestions",
                table: "AgentConversations",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Suggestions",
                table: "AgentConversations");
        }
    }
}
