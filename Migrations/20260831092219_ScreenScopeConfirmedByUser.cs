using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class ScreenScopeConfirmedByUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PHẠM VI MÀN HÌNH GỘP VỀ MỘT NGUỒN: cột PlannedScope (danh sách bullet do LLM chắt sau mỗi lượt
            // chat) bị gỡ, và bảng màn hình — Projects.ScreenScopeMap, JSON ScreenScopeRow[] — trở thành
            // nguồn duy nhất, với cờ ConfirmedByUser trên từng dòng/chức năng phân biệt phần đã rà với phần
            // vừa lộ ra. Hai lệnh dưới đây chuyển dữ liệu đang có sang đúng ngữ nghĩa mới; bỏ chúng thì mọi
            // dự án đang dở đều mất phạm vi hoặc bị hỏi lại từ đầu.

            // 1) DỰ ÁN ĐÃ CHỐT BẢNG. Mọi dòng/chức năng trong cột này đều đã đi qua tay người dùng theo định
            // nghĩa cũ ("cột khác null = đã chốt"), nên tất cả phải mang dấu. Không đóng dấu thì cổng bảng
            // màn hình mở lại ngay lượt chat kế tiếp và bắt họ rà lại trọn bảng.
            //
            // Vá bằng REPLACE trên chuỗi JSON chứ không parse: trường mới nằm CUỐI cả hai class, và
            // System.Text.Json ghi theo thứ tự khai báo, nên bản ghi cũ luôn kết thúc bằng đúng bốn khuôn
            // dưới đây — dòng màn hình bằng "AddedByUser", dòng chức năng bằng "Included". Hai khuôn không
            // giẫm lên nhau. Nếu một bản ghi nào đó lọt lưới, hậu quả là bảng ấy được bày lại một lần để
            // người dùng gật, không phải mất dữ liệu.
            migrationBuilder.Sql("""
                UPDATE Projects SET ScreenScopeMap = REPLACE(REPLACE(REPLACE(REPLACE(ScreenScopeMap,
                        '"Included":true}',     '"Included":true,"ConfirmedByUser":true}'),
                        '"Included":false}',    '"Included":false,"ConfirmedByUser":true}'),
                        '"AddedByUser":true}',  '"AddedByUser":true,"ConfirmedByUser":true}'),
                        '"AddedByUser":false}', '"AddedByUser":false,"ConfirmedByUser":true}')
                WHERE ScreenScopeMap IS NOT NULL;
                """);

            // 2) DỰ ÁN ĐANG DỞ (chắt được phạm vi nhưng chưa tới lượt bày bảng): mỗi bullet của PlannedScope
            // thành một DÒNG CHỜ DUYỆT. Bỏ bước này là xoá phạm vi của mọi buổi phỏng vấn đang chạy — cổng
            // bảng màn hình đòi bảng có mục mới mở, nên chúng sẽ đứng im cho tới khi hội thoại tình cờ nhắc
            // lại đủ số màn hình đó.
            //
            // Thứ tự dòng không được STRING_SPLIT bảo đảm (enable_ordinal chỉ có từ SQL Server 2022) và ở
            // đây điều đó không hệ trọng: bảng chưa từng hiện ra, nên chưa có thứ tự nào để giữ.
            migrationBuilder.Sql("""
                UPDATE p SET ScreenScopeMap = x.Json
                FROM Projects p
                CROSS APPLY (
                    SELECT '[' + STRING_AGG(
                        '{"Screen":"' + STRING_ESCAPE(LTRIM(RTRIM(SUBSTRING(s.value, 3, 4000))), 'json')
                        + '","Purpose":"","Functions":[],"Covers":[],"Included":true,'
                        + '"AddedByUser":false,"ConfirmedByUser":false}', ',') AS Json
                    FROM STRING_SPLIT(REPLACE(p.PlannedScope, CHAR(13), ''), CHAR(10)) s
                    WHERE LEFT(LTRIM(s.value), 2) = '- ' AND LEN(LTRIM(RTRIM(s.value))) > 2
                ) x
                WHERE p.ScreenScopeMap IS NULL AND p.PlannedScope IS NOT NULL AND x.Json IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "PlannedScope",
                table: "Projects");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlannedScope",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            // Dựng lại cột từ chính bảng màn hình — các dòng CÒN TÍCH, đúng thứ danh sách cũ chở. Phần cờ
            // ConfirmedByUser thì không có chỗ nào để về, và đó là mất mát chấp nhận được của một đường lùi:
            // bản cũ vốn không phân biệt được "đã rà" với "vừa lộ ra".
            migrationBuilder.Sql("""
                UPDATE p SET PlannedScope = x.Bullets
                FROM Projects p
                CROSS APPLY (
                    SELECT STRING_AGG('- ' + JSON_VALUE(r.value, '$.Screen'), CHAR(10)) AS Bullets
                    FROM OPENJSON(p.ScreenScopeMap) r
                    WHERE JSON_VALUE(r.value, '$.Included') = 'true'
                ) x
                WHERE p.ScreenScopeMap IS NOT NULL AND ISJSON(p.ScreenScopeMap) = 1 AND x.Bullets IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE Projects SET ScreenScopeMap = REPLACE(REPLACE(ScreenScopeMap,
                        ',"ConfirmedByUser":true}',  '}'),
                        ',"ConfirmedByUser":false}', '}')
                WHERE ScreenScopeMap IS NOT NULL;
                """);
        }
    }
}
