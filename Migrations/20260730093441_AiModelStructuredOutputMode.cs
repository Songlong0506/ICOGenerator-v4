using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <summary>
    /// Cờ bool SupportsStructuredOutput → enum StructuredOutputMode (None / JsonObject / JsonSchema).
    /// Bản scaffold mặc định DROP cột cũ TRƯỚC khi thêm cột mới, tức là xoá trắng cấu hình của mọi model
    /// đang chạy; ở đây thêm cột trước, chép giá trị sang, rồi mới drop.
    /// Cột cũ chỉ mang nghĩa "gửi json_schema" nên true → JsonSchema: giữ nguyên hành vi đang chạy, kể cả khi
    /// đó là cấu hình sai với DeepSeek — LlmClient nay tự hạ cấp và ghi cảnh báo thay vì để lỗi 400 làm chết
    /// tính năng. Diễn giải lại hộ người dùng thành JsonObject sẽ là đổi cấu hình sau lưng họ.
    /// </summary>
    public partial class AiModelStructuredOutputMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StructuredOutputMode",
                table: "AiModels",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.Sql(
                "UPDATE AiModels SET StructuredOutputMode = CASE WHEN SupportsStructuredOutput = 1 THEN 'JsonSchema' ELSE 'None' END;");

            migrationBuilder.DropColumn(
                name: "SupportsStructuredOutput",
                table: "AiModels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SupportsStructuredOutput",
                table: "AiModels",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Cột bool cũ không diễn tả được JsonObject: chỉ JsonSchema quay về true, JsonObject bị hạ xuống
            // false (mất cấu hình — đúng cái giới hạn khiến enum này ra đời).
            migrationBuilder.Sql(
                "UPDATE AiModels SET SupportsStructuredOutput = CASE WHEN StructuredOutputMode = 'JsonSchema' THEN 1 ELSE 0 END;");

            migrationBuilder.DropColumn(
                name: "StructuredOutputMode",
                table: "AiModels");
        }
    }
}
