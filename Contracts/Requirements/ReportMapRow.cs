namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// MỘT dòng của "bảng báo cáo / thống kê": một báo cáo ứng dụng phải có, và đủ dữ kiện để nó trở thành một
/// màn hình thật thay vì một câu trong văn xuôi.
///
/// <para>
/// <b>Vì sao là bảng chứ không phải một đoạn văn.</b> Câu trả lời thật của nhóm «Báo cáo / thống kê»
/// luôn là một DANH SÁCH — "cuối tháng sếp cần biết ai chưa đi học, và mỗi đơn vị chi bao nhiêu" là hai
/// báo cáo, không phải một câu. Gom cả danh sách vào một đoạn thì mỗi mục mất đi phần "lấy số từ đâu" và
/// "gộp theo gì", và bước sinh spec phải tự đoán lại cả hai.
/// </para>
///
/// <para>
/// <b>Bảng do BA ráp, người dùng không rà nó.</b> Nhóm lên <c>[RÕ]</c> bằng chính lựa chọn của người dùng
/// (chip BA đề xuất + phần gõ thêm), rồi <c>ReportMapDraftService</c> ráp các báo cáo đã chọn thành bảng
/// này và gieo mỗi dòng thành một MÀN HÌNH chờ duyệt. Người dùng rà chúng một lần, ở bảng màn hình — bày
/// thêm một bảng báo cáo để "chốt lại" là hỏi ba lần cho một danh sách.
/// </para>
///
/// <para>
/// <b>Không có cột "ai xem".</b> Đó là chủ ý, không phải thiếu sót: một báo cáo LÀ một màn hình
/// (<c>ReportMapBuilder.ReportScreenAdditions</c> gieo nó vào bảng màn hình TRƯỚC lần bày đầu của bảng ấy),
/// nên nó đi qua bảng màn hình rồi thành DÒNG của bảng phân quyền — nơi "ai xem" đã có cột riêng kèm PHẠM
/// VI DỮ LIỆU ("của mình" / "của đơn vị" / "tất cả"). Thêm một cột vai trò ở đây là dựng danh sách vai trò
/// thứ hai trong cùng một buổi phỏng vấn, và hai danh sách lệch nhau thì không tầng nào phía sau biết tin
/// bên nào.
/// </para>
/// </summary>
public class ReportMapRow
{
    /// <summary>
    /// Tên báo cáo, đọc được như MỘT MÀN HÌNH ("Báo cáo tổng hợp ngày phép còn lại") — vì nó sẽ thành một
    /// mục phạm vi thật. Một cái tên trống nghĩa ("Thống kê") thì tới bảng màn hình không ai rà nổi nó.
    /// </summary>
    public string Report { get; set; } = "";

    /// <summary>
    /// Báo cáo này TRẢ LỜI CÂU HỎI GÌ — mục đích viết bằng lời người dùng ("để biết tháng này ai chưa đi
    /// học"), không phải mô tả chức năng bằng từ vựng BA.
    ///
    /// <para>
    /// Ranh giới đó là chủ ý: người dùng nghiệp vụ không "mô tả chức năng", và bắt họ làm việc đó là đảo
    /// vai — phần diễn giải thành chức năng là việc của bước sinh spec, còn thứ chỉ họ mới biết là báo cáo
    /// ấy dùng để quyết định điều gì.
    /// </para>
    /// </summary>
    public string Question { get; set; } = "";

    /// <summary>
    /// Số liệu lấy từ BẢN GHI nào ("Leave Request", "Training Plan") — chữ tự do, BA ráp từ hội thoại.
    /// Bước sinh spec đọc nó như một gợi ý để màn hình báo cáo tính số từ đúng các bản
    /// ghi đó. Rỗng = hội thoại chưa nói rõ lấy từ đâu.
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// GỘP / LỌC theo cái gì: kỳ báo cáo, đơn vị, trạng thái, người phụ trách… Đây là cột phân biệt một báo
    /// cáo thật với một bảng đổ dữ liệu ra màn hình, và nó có đường đi riêng ở spec (bộ lọc thật của màn
    /// hình báo cáo trong "## 6. Screens To Generate"). Ngăn bằng dấu chấm phẩy hoặc xuống dòng.
    /// </summary>
    public string Breakdown { get; set; } = "";

    /// <summary>
    /// Ứng dụng CÓ cần báo cáo này không. Mọi dòng ráp ra đều được giữ; chỉ NGƯỜI DÙNG hạ được cờ này, bằng
    /// cách bỏ tích màn hình của báo cáo ở bảng màn hình (<c>ReportMapBuilder.DropUnscreened</c>). Hạ cờ chứ
    /// không xóa dòng: báo cáo bị loại vẫn phải được kể ra trong khối ngữ cảnh, nếu không BA đề xuất lại nó.
    /// </summary>
    public bool Included { get; set; } = true;
}
