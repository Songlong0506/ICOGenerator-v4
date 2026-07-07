namespace ICOGenerator.Domain;

/// <summary>
/// Ma trận truy vết của MỘT project: mỗi yêu cầu trong tài liệu nghiệp vụ (BRD, hoặc Product Brief khi
/// chưa có BRD) được nối tới user story / file code / bằng chứng test tương ứng — và cái gì đang "mồ côi".
/// Kết quả do LLM phân tích (xem TraceabilityMatrixBuilder) và lưu dạng JSON đã chuẩn hoá trong
/// <see cref="MatrixJson"/>; mỗi project chỉ giữ BẢN MỚI NHẤT (một dòng, ghi đè khi bấm phân tích lại) —
/// lịch sử chi tiết không có giá trị vì ma trận luôn nên đọc trên trạng thái tài liệu/code hiện hành.
/// </summary>
public class ProjectTraceability
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;

    /// <summary>JSON đã chuẩn hoá của ma trận (serialize lại từ kết quả parse — không lưu output thô của model).</summary>
    public string MatrixJson { get; set; } = string.Empty;

    /// <summary>Snapshot tên model đã phân tích (không FK — như mọi tham chiếu model trong nhóm eval/log).</summary>
    public string ModelName { get; set; } = string.Empty;

    public int TotalTokens { get; set; }

    public string? GeneratedByUsername { get; set; }

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
