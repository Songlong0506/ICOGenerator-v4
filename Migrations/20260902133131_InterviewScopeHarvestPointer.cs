using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class InterviewScopeHarvestPointer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CON TRỎ RIÊNG cho lượt chắt lọc phạm vi màn hình, tách khỏi con trỏ của "triển vọng phỏng
            // vấn" vì hai lượt nay chạy theo hai nhịp khác nhau (xem InterviewScopeService).
            //
            // Mặc định 0 cho MỌI dự án đang có, kể cả dự án đã chốt bảng màn hình từ lâu, và đó là lựa chọn
            // cố ý: 0 nghĩa là "chưa gộp lượt nào", nên lần chạy đầu của dự án cũ sẽ đọc lại trọn hội thoại
            // một lần rồi mới theo lô. Phần đã có trong bảng không vì thế mà nhân đôi — Merge chỉ THÊM thứ
            // chưa có, và dòng người dùng đã bỏ tích thì nó chặn hẳn.
            migrationBuilder.AddColumn<int>(
                name: "InterviewScopeHarvestedTurnCount",
                table: "Projects",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InterviewScopeHarvestedTurnCount",
                table: "Projects");
        }
    }
}
