using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Một lượt KIỂM TOÁN TRỌNG TÀI: lấy mẫu các lời gọi trọng tài đã ghi trong <c>AgentModelCallLogs</c> (mục
/// đích <see cref="Purpose"/>, vd <c>BATurnJudge</c>), dựng đáp án chuẩn cho từng lượt rồi đo mỗi model phán
/// đúng tới đâu — theo TỪNG trường của phán quyết, tách "phán thừa" (với <c>repeats</c>/<c>emptyChips</c>/
/// <c>drop</c> là XOÁ OAN) khỏi "phán sót".
///
/// <para>
/// Hai chế độ, phân biệt bằng <see cref="CandidateModelId"/>:
/// <list type="bullet">
///   <item><b>Chạy lại</b> (có model ứng viên): gửi lại NGUYÊN VĂN đầu vào của từng lượt đã log cho model ứng
///         viên, so với phán quyết đã lưu. Trùng nhau ⇒ coi là đáp án (trừ khi <see cref="ArbitrateAll"/>); lệch
///         ⇒ hỏi model trọng tài phân xử. Dùng để thử một model trọng tài mới TRƯỚC khi bật nó.</item>
///   <item><b>Kiểm toán</b> (không có ứng viên): hỏi model trọng tài cho MỌI lượt lấy mẫu. Dùng để chấm một
///         model đã chạy thật một thời gian — lọc bằng <see cref="SourceModelId"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// Model tham chiếu bằng Guid + snapshot tên, KHÔNG FK — như <see cref="EvalRun"/>. Trạng thái dùng chung
/// <see cref="EvalRunStatus"/>: vòng đời y hệt một run eval (Queued → Running → Completed/Failed/Cancelled,
/// huỷ là cờ hợp tác).
/// </para>
/// </summary>
public class JudgeAuditRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Mục đích trong AI Call Logs được kiểm toán: <c>BATurnJudge</c> hoặc <c>BACoverageQuestionJudge</c>.</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>Chỉ lấy lượt do model này phán (so khớp <c>AgentModelCallLog.ModelId</c>); null = mọi model.</summary>
    public string? SourceModelId { get; set; }

    /// <summary>Khoảng thời gian lấy mẫu (UTC, nửa mở [From, To)); null = không chặn đầu đó.</summary>
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    /// <summary>Số lượt muốn lấy mẫu; số thật (ít hơn khi log không đủ) nằm ở <see cref="CaseCount"/>.</summary>
    public int SampleSize { get; set; }

    /// <summary>Model ứng viên chạy lại; null = chế độ kiểm toán.</summary>
    public Guid? CandidateModelId { get; set; }
    public string? CandidateModelName { get; set; }

    public Guid ArbiterModelId { get; set; }
    public string ArbiterModelName { get; set; } = string.Empty;

    /// <summary>
    /// Chế độ chạy lại: hỏi trọng tài cả những lượt mà hai phán quyết TRÙNG nhau. Tắt (mặc định) thì rẻ hơn
    /// nhiều nhưng một lỗi mà cả hai model cùng mắc sẽ không bị đếm. Chế độ kiểm toán luôn hỏi trọng tài.
    /// </summary>
    public bool ArbitrateAll { get; set; }

    public string? Note { get; set; }

    public EvalRunStatus Status { get; set; } = EvalRunStatus.Queued;

    public int CaseCount { get; set; }
    public int CompletedCount { get; set; }

    /// <summary>Số lượt mà ít nhất một phán quyết lệch đáp án chuẩn — con số đọc nhanh ở danh sách run.</summary>
    public int DisagreementCount { get; set; }

    /// <summary>Tổng chi phí USD (ứng viên + trọng tài), theo đơn giá model lúc chạy.</summary>
    public decimal TotalCost { get; set; }

    public string? Error { get; set; }

    /// <summary>Cờ huỷ hợp tác — runner đọc lại giữa hai lượt, như <see cref="EvalRun.CancelRequestedAt"/>.</summary>
    public DateTime? CancelRequestedAt { get; set; }

    public string? CreatedByUsername { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public List<JudgeAuditCase> Cases { get; set; } = new();
}
