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
    Sent
}
