namespace ICOGenerator.Application.Projects;

/// <summary>
/// Phạm vi nhân bản một dự án. KHÔNG lưu DB (chỉ đi từ form vào use case) nên không dính luật "enum đã
/// lưu dạng chuỗi thì đừng đổi tên" — vì vậy nó ở đây thay vì Domain/Enums.
/// </summary>
public enum ProjectCloneScope
{
    /// <summary>
    /// Chép nguyên trạng: hội thoại, file nguồn, tài liệu (kèm lịch sử revision), workflow, ghi chú POC và
    /// cả thư mục workspace. Dùng để RẼ NHÁNH từ đúng chặng dự án gốc đang đứng.
    /// </summary>
    Full = 0,

    /// <summary>
    /// Chỉ chép phần yêu cầu: trí nhớ hội thoại BA, các lượt chat, file nguồn và sáu bảng đã chốt. Không có
    /// tài liệu / workflow / POC nào — bản sao chạy lại delivery từ đầu trên cùng một buổi phỏng vấn.
    /// </summary>
    RequirementOnly = 1
}
