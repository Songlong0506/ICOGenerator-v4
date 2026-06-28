using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Một dấu vết thay đổi cấu hình "blast-radius cao" (Settings, Role, Agent, Model): AI/đổi-gì/khi-nào.
/// Một bảng duy nhất cho cả bốn loại — lọc theo <see cref="Category"/> — để debug "ai vừa động vào cấu hình"
/// nhanh ở một chỗ. KHÁC với AgentModelCallLog (log runtime của lời gọi LLM, không phải đổi cấu hình).
///
/// ⚠️ <see cref="BeforeJson"/>/<see cref="AfterJson"/> KHÔNG được chứa secret nguyên văn (API key, connection
/// string...). AuditLogger tự che (redact) các trường nhạy cảm trước khi ghi.
/// </summary>
public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public AuditCategory Category { get; set; }
    public AuditAction Action { get; set; }

    /// <summary>Khóa của đối tượng bị đổi: Guid (Agent/Model) dạng chuỗi, tên role, hoặc "AppSettings".</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>Mô tả người-đọc-được, ví dụ "Cập nhật AI Model GPT-4o".</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Username người thực hiện (lấy từ claim đăng nhập); "system" nếu không có ngữ cảnh request.</summary>
    public string ActorUsername { get; set; } = string.Empty;
    public string ActorRole { get; set; } = string.Empty;

    /// <summary>Ảnh chụp trạng thái trước/sau ở dạng JSON đã che secret. Null khi không áp dụng (vd Create).</summary>
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
