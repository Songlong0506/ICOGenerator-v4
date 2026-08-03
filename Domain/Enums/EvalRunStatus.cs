namespace ICOGenerator.Domain.Enums;

public enum EvalRunStatus
{
    Queued,
    Running,
    Completed,
    Failed,

    /// <summary>
    /// Người dùng bấm huỷ. Khác <see cref="Failed"/> ở chỗ đây là quyết định CÓ CHỦ Ý — kết quả của các
    /// scenario đã chạy xong vẫn giữ nguyên (và vẫn tính vào điểm TB), chỉ là bộ scenario không chạy hết.
    /// </summary>
    Cancelled
}
