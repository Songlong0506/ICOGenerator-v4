using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Loại phản hồi người dùng gửi qua màn hình Feedback. Lưu xuống DB dạng chuỗi (tên enum) nên ĐỪNG
/// đổi tên giá trị đã có (sẽ làm "mồ côi" dữ liệu cũ); thêm giá trị mới ở cuối thì an toàn.
/// </summary>
public enum FeedbackType
{
    [Description("Báo lỗi (Bug)")]
    Bug = 1,
    [Description("Góp ý / Đề xuất")]
    Suggestion = 2,
    [Description("Chia sẻ trải nghiệm")]
    Experience = 3,
    [Description("Khác")]
    Other = 4
}
