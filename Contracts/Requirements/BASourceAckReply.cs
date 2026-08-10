namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Nội dung các HÌNH của một tài liệu nguồn, do BA đọc trực tiếp từ ảnh ở lượt xác nhận tài liệu và ghi lại
/// thành chữ. Khác <see cref="BAChatReply.Message"/> ở chỗ đây KHÔNG phải thứ hiển thị cho người dùng: nó là
/// bản thay thế cho chính các tấm ảnh ở những lượt chat sau (xem <c>ProjectSourceFile.VisionSummary</c>),
/// nên nó cần chi tiết và bám sát mặt chữ trên hình, còn Message thì cần ngắn gọn dễ đọc.
/// </summary>
public class SourceVisionNote
{
    /// <summary>Tên file đúng như trong "[Nguồn: ...]" — khóa để ghép ghi chú về đúng nguồn.</summary>
    public string FileName { get; set; } = "";

    /// <summary>Nội dung đọc được của từng [Hình n] trong file đó.</summary>
    public string Note { get; set; } = "";
}

/// <summary>
/// Lượt BA xác nhận đã đọc tài liệu nguồn: vẫn là một <see cref="BAChatReply"/> bình thường (message +
/// suggestions hiển thị trong chat), CỘNG phần <see cref="SourceNotes"/> chỉ dùng nội bộ.
///
/// Vì sao gộp vào một lời gọi thay vì gọi riêng một lượt "mô tả hình": lượt này vốn ĐÃ có toàn bộ ảnh trên
/// tay và model vốn ĐÃ phải đọc hết chúng để tóm tắt — bắt nó ghi thêm phần chi tiết là gần như miễn phí,
/// còn gọi lại lần nữa là trả tiền upload ảnh thêm một lượt.
/// </summary>
public class BASourceAckReply : BAChatReply
{
    /// <summary>
    /// Mỗi tài liệu có hình một mục. Rỗng là chuyện bình thường (model không bật structured output, hoặc
    /// nguồn không có hình nào) — khi đó ảnh tiếp tục được gửi kèm ở các lượt sau như trước.
    /// </summary>
    public List<SourceVisionNote> SourceNotes { get; set; } = new();

    /// <summary>
    /// BẢNG CỘT đề xuất cho các nguồn dạng BẢNG TÍNH của lượt này: mỗi cột một dòng, kèm cách hiểu của BA
    /// và đề xuất cột đó có thuộc ứng dụng mới hay không. Người dùng tích lại rồi gửi — xem
    /// <see cref="SourceColumnNote"/>.
    ///
    /// <para>
    /// Gộp vào chính lượt này thay vì hỏi ở một lượt chat sau, vì đây là lượt DUY NHẤT model đã cầm trên
    /// tay khối "Thống kê cột" của cả bảng (mọi giá trị phân biệt kèm số dòng) — thứ cần để đoán nghĩa
    /// từng cột. Hỏi ở lượt sau thì phần lớn dữ liệu đó đã bị nén khỏi ngữ cảnh và BA chỉ còn đoán bằng
    /// tên cột.
    /// </para>
    ///
    /// <para>
    /// Rỗng là hợp lệ (nguồn không có bảng tính nào, hoặc model không bật structured output): khi đó không
    /// có bảng nào hiện ra và luồng chạy đúng như trước.
    /// </para>
    /// </summary>
    public List<SourceColumnNote> Columns { get; set; } = new();
}
