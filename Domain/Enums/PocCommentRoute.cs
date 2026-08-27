namespace ICOGenerator.Domain.Enums;

/// <summary>
/// ĐƯỜNG XỬ LÝ mà một ghi chú đã được gửi đi — tách khỏi <see cref="PocCommentStatus"/> (vòng đời).
/// Hai thứ này từng bị gộp làm một: <c>RoutedToRequirement</c> vừa là trạng thái vừa là đường đi, nên
/// ghi chú đi đường tài liệu không bao giờ có được trạng thái "đã sửa xong" và bảng lịch sử không trả
/// lời được câu hỏi đơn giản nhất: điểm này ai xử lý.
/// <para><c>null</c> = chưa gửi đi đâu (ghi chú còn <see cref="PocCommentStatus.Open"/>).</para>
/// </summary>
public enum PocCommentRoute
{
    /// <summary>Nhờ đội Dev chỉnh bản demo — vào <see cref="Domain.AgentTask.RevisionFeedback"/> của một vòng sửa POC.</summary>
    FixPoc,

    /// <summary>Gửi về Requirement để BA sửa tài liệu — vào hội thoại BA, Brief/Spec soạn lại rồi POC dựng lại.</summary>
    Requirement
}
