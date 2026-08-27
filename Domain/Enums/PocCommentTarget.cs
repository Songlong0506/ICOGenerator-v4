namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Ghi chú của người review nói VỀ cái gì. Bảng <see cref="Domain.PocComment"/> giữ cả hai loại vì
/// chúng là CÙNG MỘT dòng lịch sử: người yêu cầu chê bản mô tả (Brief) hay chê bản demo (POC) thì đều
/// là "điểm chưa đạt ở phiên bản Brief thứ n", và trang POC Review đọc lại cả hai trong một bảng.
/// Trước đây ghi chú Brief chỉ được nối thành một lượt chat rồi biến mất — sau khi Brief lên V{n+1}
/// không còn cách nào biết V{n} từng bị chê gì.
/// </summary>
public enum PocCommentTarget
{
    /// <summary>Ghim lên một phần tử trong POC demo (trang Projects/PocReview).</summary>
    Poc,

    /// <summary>Ghim lên một đoạn (hoặc cả bản) Product Brief ở popup xem trước — xem ReviseBriefFromNotesUseCase.</summary>
    Brief
}
