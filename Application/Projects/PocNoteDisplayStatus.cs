using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Application.Projects;

/// <summary>
/// Ba trạng thái người dùng nhìn thấy trên bảng ghi chú của POC Review / trang khách. Đây là một PHÉP CHIẾU
/// của <see cref="PocCommentStatus"/> (năm giá trị, lưu DB dạng chuỗi — không đổi tên được), không phải một
/// cột mới: người review chỉ hỏi "mình gửi chưa / đã có người làm chưa / đã xong chưa", còn chi tiết đường
/// đi (Dev hay BA, hoãn hay không) nằm ở tooltip của ô Status.
/// </summary>
public static class PocNoteDisplayStatus
{
    /// <summary>Chưa gửi cho đội xử lý.</summary>
    public const string Draft = "Draft";

    /// <summary>Đã gửi đi, chưa được áp vào bản demo.</summary>
    public const string Waiting = "Waiting";

    /// <summary>Đã được áp: vòng sửa POC mang nó đã chạy xong, hoặc tài liệu nó chê đã được duyệt lại.</summary>
    public const string Apply = "Apply";

    public static string Of(PocComment comment)
    {
        // Ghi chú Brief đi thẳng vào hội thoại BA ngay lúc gõ (RoutedToRequirement từ đầu) và đóng dấu
        // "draft"; ApproveRequirementUseCase nâng nó lên V{n} đúng lúc bản nháp nó chê được duyệt — tức là
        // lúc góp ý đã nằm trong tài liệu chính thức. Không có cột trạng thái nào khác nói điều đó.
        if (comment.Target == PocCommentTarget.Brief)
            return comment.BriefVersion == BriefVersionResolver.DraftVersion ? Waiting : Apply;

        return comment.Status switch
        {
            PocCommentStatus.Open => Draft,
            PocCommentStatus.Addressed => Apply,
            // Sent (Dev đang sửa), Deferred (chờ bản demo dựng lại), RoutedToRequirement (BA đang sửa tài
            // liệu — PocDeferredFeedbackService.MarkRoutedAppliedAsync đóng nó khi bản demo dựng lại xong).
            _ => Waiting
        };
    }

    /// <summary>
    /// Ghi chú còn RÚT được không (bỏ qua chuyện quyền — đó là việc của người gọi). Chỉ hai ca: chưa gửi
    /// (<c>Open</c>), và đã gửi nhưng còn HOÃN chờ bản demo dựng lại (<c>Deferred</c>) — chưa agent nào cầm
    /// nó. Ghi chú đã tới tay Dev (<c>Sent</c>) hay BA (<c>RoutedToRequirement</c>) thì việc đã xảy ra, xoá
    /// dòng không dừng được agent mà chỉ làm lệch lịch sử. Một định nghĩa cho cả nút 🗑 lẫn use case thu hồi.
    /// </summary>
    public static bool IsWithdrawable(PocComment comment) =>
        comment.Target == PocCommentTarget.Poc
        && comment.WithdrawnAtUtc == null
        && comment.Status is PocCommentStatus.Open or PocCommentStatus.Deferred;
}
