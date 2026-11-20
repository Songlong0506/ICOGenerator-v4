using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Một dòng log ứng dụng (mặc định chỉ Warning trở lên) do <c>DatabaseLogSink</c> chép từ Serilog vào DB,
/// để người có quyền <c>SystemLogView</c> tra lỗi ngay trên màn hình System Logs thay vì xin file log
/// trên máy chủ. File log vẫn là nguồn đầy đủ: DB chỉ là bản sao các dòng đáng chú ý.
/// KHÁC <see cref="AuditLog"/> (ai đổi cấu hình) và <see cref="AgentModelCallLog"/> (lời gọi model).
/// </summary>
public class SystemLog
{
    /// <summary>Khoá tăng dần: bảng chỉ chèn nối đuôi, Guid ngẫu nhiên sẽ làm phân mảnh clustered index.</summary>
    public long Id { get; set; }

    /// <summary>Thời điểm sự kiện log xảy ra (UTC) — không phải lúc dòng được ghi xuống DB.</summary>
    public DateTime Timestamp { get; set; }

    public SystemLogLevel Level { get; set; }

    /// <summary>Tên logger (thường là tên class ghi log), vd <c>ICOGenerator.Services.Workflows.AgentTaskWorker</c>.</summary>
    public string? SourceContext { get; set; }

    /// <summary>Thông điệp đã render tham số.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary><c>Exception.ToString()</c> (kiểu, message, stack trace, inner exception) nếu có.</summary>
    public string? Exception { get; set; }

    /// <summary>Đường dẫn HTTP của request đang xử lý khi log xảy ra; null với log của worker nền.</summary>
    public string? RequestPath { get; set; }

    /// <summary>Mọi thuộc tính có cấu trúc khác của sự kiện (ProjectId, TraceId, MachineName...) dạng JSON.</summary>
    public string? Properties { get; set; }
}
