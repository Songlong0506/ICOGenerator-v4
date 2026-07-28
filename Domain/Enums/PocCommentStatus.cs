namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Vòng đời một ghi chú ghim trực tiếp trên POC (<see cref="PocComment"/>): người xem ghim khi
/// review, người duyệt gom các ghi chú Open vào một "Yêu cầu chỉnh sửa" ở cổng POC — lúc đó ghi chú
/// chuyển Sent để không bị gửi lặp ở vòng chỉnh sửa sau.
/// </summary>
public enum PocCommentStatus
{
    /// <summary>Mới ghim — chưa được gửi cho agent trong yêu cầu chỉnh sửa nào.</summary>
    Open,

    /// <summary>Đã gộp vào một yêu cầu chỉnh sửa POC (Developer agent đã/đang xử lý).</summary>
    Sent,

    /// <summary>
    /// Vòng chỉnh sửa POC đã chạy XONG với ghi chú này trong tay: Developer agent đã có cơ hội sửa. Trạng
    /// thái này tồn tại để người review vòng sau biết "cái nào đã được đụng tới" thay vì phải rà lại từ
    /// đầu cả bản demo — và để họ mở lại (về Open) đúng những ghi chú CHƯA đạt cho vòng sau.
    /// </summary>
    Addressed,

    /// <summary>
    /// Đã gửi NGƯỢC về bước Requirement: ghi chú này phản ánh HIỂU SAI YÊU CẦU (không phải lỗi HTML), nên
    /// được đưa vào hội thoại BA để soạn lại tài liệu của CHÍNH dự án — POC dựng lại từ tài liệu đã sửa.
    /// Xem RoutePocFeedbackToRequirementUseCase.
    /// </summary>
    RoutedToRequirement
}
