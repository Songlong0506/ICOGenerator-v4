namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Mức của một dòng log ứng dụng lưu trong bảng <c>SystemLogs</c>. Tên giá trị trùng với
/// <c>Serilog.Events.LogEventLevel</c> để ánh xạ bằng tên — Domain không phụ thuộc Serilog.
/// Lưu DB dạng chuỗi ⇒ không đổi tên giá trị đã có.
/// </summary>
public enum SystemLogLevel
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Fatal
}
