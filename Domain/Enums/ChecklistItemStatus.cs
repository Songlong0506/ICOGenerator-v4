namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Một mục checklist học được có đang được nạp vào prompt BA hay không — và nếu không thì AI hay người
/// đã tắt nó. Tắt chứ không xóa: mục vẫn nằm đó để (a) người dùng bật lại được, (b) vòng harvest sau
/// nhìn thấy và KHÔNG đề xuất lại đúng bài học người dùng vừa loại.
/// </summary>
public enum ChecklistItemStatus
{
    /// <summary>Đang được nạp vào prompt ở mọi lượt chat của các dự án cùng bucket.</summary>
    Active = 0,

    /// <summary>Người dùng tự bỏ tick trên trang quản trị — bài học sai/không muốn BA hỏi nữa.</summary>
    DisabledByUser = 1,

    /// <summary>
    /// Hệ thống tự tắt vì bucket đã vượt trần số mục đang dùng: bài học cũ nhất nhường chỗ cho bài học
    /// mới. Người dùng bật lại được (khi đó mục cũ khác sẽ bị đẩy ra ở vòng harvest sau).
    /// </summary>
    DisabledByOverflow = 2
}
