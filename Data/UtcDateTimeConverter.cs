using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ICOGenerator.Data;

// SQL Server datetime2 KHÔNG lưu DateTimeKind: mọi DateTime đọc lên đều về Unspecified. Khi controller trả JSON,
// System.Text.Json serialize Unspecified mà KHÔNG kèm hậu tố 'Z', nên `new Date(...)` ở trình duyệt hiểu nhầm mốc
// UTC thành giờ local và lệch đúng bằng offset của máy (VN +7 → popup AI Call Logs hiện 3PM thay vì 10PM).
// Toàn bộ app ghi thời gian bằng DateTime.UtcNow, nên gắn lại Kind=Utc khi đọc để JSON có 'Z' chuẩn ISO-8601
// và trình duyệt tự quy đổi về giờ địa phương. Chiều ghi giữ nguyên (giá trị vốn đã là UTC), cột vẫn là datetime2
// nên không phát sinh migration. Dùng được cho cả cột DateTime? (EF tự bỏ qua giá trị null).
public class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter() : base(
        v => v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    { }
}
