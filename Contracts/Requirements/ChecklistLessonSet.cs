namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Kết quả một vòng rút kinh nghiệm về BỘ CÂU HỎI của BA: các bài học MỚI đề xuất thêm vào checklist
/// học được. Vòng harvest chỉ ĐỀ XUẤT THÊM — không viết lại cả danh sách như trước, để mục đã có giữ
/// nguyên định danh, lý do và trạng thái bật/tắt người dùng đã đặt.
/// </summary>
public class ChecklistLessonSet
{
    public List<ChecklistLesson> Items { get; set; } = new();
}

/// <summary>Một bài học: hỏi thêm điều gì (<see cref="Text"/>) + vì sao rút ra được (<see cref="Rationale"/>, <see cref="Evidence"/>).</summary>
public class ChecklistLesson
{
    /// <summary>Mục checklist đã khái quát hoá — ĐÚNG phần được nạp vào prompt BA ở các dự án sau.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Một câu giải thích vì sao đây là khoảng trống của bộ câu hỏi. Chỉ hiển thị cho người quản trị.</summary>
    public string Rationale { get; set; } = string.Empty;

    /// <summary>Trích ngắn bằng chứng gốc (lượt người dùng tự nêu / ghi chú POC) để truy nguồn bài học.</summary>
    public string Evidence { get; set; } = string.Empty;
}
