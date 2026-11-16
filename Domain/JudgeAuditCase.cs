namespace ICOGenerator.Domain;

/// <summary>
/// MỘT lượt trọng tài trong một <see cref="JudgeAuditRun"/>: phán quyết đã lưu, phán quyết chạy lại (nếu có),
/// đáp án chuẩn và lời giải thích của trọng tài.
///
/// <para>
/// Các phán quyết lưu dưới dạng danh sách <b>khẳng định</b> chuẩn hoá (JSON mảng <c>[{field, key}]</c>, vd
/// <c>{"field":"repeats","key":"Q2"}</c>) chứ không phải JSON thô của model: chấm điểm là phép so tập hợp,
/// và dạng này đọc lại được bất kể model trả về hoa/thường, thứ tự hay hàng rào ```json ra sao. Phán quyết
/// KHÔNG ĐỌC ĐƯỢC được lưu là tập rỗng kèm cờ <c>...Unreadable</c> — đúng hiệu lực của nó ở production
/// (fail-open: không chốt chặn nào động tay).
/// </para>
///
/// <para>
/// <see cref="SourceLogId"/> trỏ về dòng log gốc, KHÔNG FK: log chứa đầu vào (trích hội thoại) và bị xoá theo
/// project; mất log chỉ mất phần xem lại đầu vào, điểm vẫn còn. Đầu vào không được chép sang đây — bảng log
/// đã mã hoá at rest, chép ra là thêm một bản sao phải giữ. Các cột chứa chữ của hội thoại (chip nằm trong
/// khẳng định, lời giải thích của trọng tài) mã hoá bằng cùng converter với <c>AgentModelCallLog</c>.
/// </para>
/// </summary>
public class JudgeAuditCase
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid JudgeAuditRunId { get; set; }
    public JudgeAuditRun JudgeAuditRun { get; set; } = default!;

    public Guid SourceLogId { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>Model đã phán lượt này ở production (<c>AgentModelCallLog.ModelId</c>).</summary>
    public string SourceModelId { get; set; } = string.Empty;
    public DateTime SourceCreatedAt { get; set; }

    public string BaselineClaimsJson { get; set; } = "[]";
    public bool BaselineUnreadable { get; set; }

    /// <summary>Null ở chế độ kiểm toán, hoặc khi lời gọi ứng viên lỗi (xem <see cref="Error"/>).</summary>
    public string? CandidateClaimsJson { get; set; }
    public bool CandidateUnreadable { get; set; }

    /// <summary>Đáp án chuẩn. Null khi lượt lỗi — lượt đó không tính vào điểm.</summary>
    public string? GoldClaimsJson { get; set; }

    /// <summary>Đáp án chuẩn do trọng tài đưa ra (false = hai phán quyết trùng nhau, lấy luôn làm đáp án).</summary>
    public bool ArbiterUsed { get; set; }
    public string? ArbiterNotes { get; set; }

    /// <summary>Ít nhất một phán quyết lệch đáp án chuẩn.</summary>
    public bool Disagreement { get; set; }

    public string? Error { get; set; }

    public decimal Cost { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
