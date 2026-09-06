using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class StageRevisionLessonQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hàng đợi vòng học từ NHẬN XÉT ở cổng duyệt của pipeline giao hàng: cờ bật khi người duyệt bấm
            // duyệt một bước đã từng bị họ yêu cầu chỉnh sửa. Nằm trên AgentTask (không phải Project như các
            // hàng đợi của BA) vì mỗi task đã mang sẵn nhận xét + vai của nó, và một lần drain có thể phải
            // xử lý nhiều bước của nhiều vai. Xem StageRevisionMemoryService.
            migrationBuilder.AddColumn<bool>(
                name: "PendingLessonHarvest",
                table: "AgentTasks",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingLessonHarvest",
                table: "AgentTasks");
        }
    }
}
