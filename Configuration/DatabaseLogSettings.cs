using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Configuration;

/// <summary>
/// Section <c>DatabaseLog</c>: chép các dòng log từ mức <see cref="MinimumLevel"/> trở lên vào bảng
/// <c>SystemLogs</c> (xem <c>Services/Logging/DatabaseLogSink</c>). Độc lập với <c>Serilog:MinimumLevel</c>
/// — file/console vẫn ghi Information, DB chỉ giữ phần đáng chú ý.
/// Section này cố ý KHÔNG có trong <c>appsettings.json</c>: mặc định dưới đây là cấu hình chuẩn; cần đổi thì
/// thêm section vào appsettings hoặc đặt biến môi trường (<c>DatabaseLog__Enabled=false</c>...).
/// </summary>
public sealed class DatabaseLogSettings
{
    public const string SectionName = "DatabaseLog";

    public bool Enabled { get; set; } = true;

    public SystemLogLevel MinimumLevel { get; set; } = SystemLogLevel.Warning;

    /// <summary>Số ngày giữ log trong DB; dòng cũ hơn bị xoá định kỳ. ≤ 0 ⇒ không tự xoá.</summary>
    public int RetentionDays { get; set; } = 30;
}
