namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// MỘT dòng của "bảng báo cáo / thống kê": một báo cáo ứng dụng phải có, và đủ dữ kiện để nó trở thành một
/// màn hình thật thay vì một câu trong văn xuôi.
///
/// <para>
/// <b>Vì sao là bảng chứ không phải một ô kể tự do.</b> Câu trả lời thật của nhóm «Báo cáo / thống kê»
/// luôn là một DANH SÁCH — "cuối tháng sếp cần biết ai chưa đi học, và mỗi đơn vị chi bao nhiêu" là hai
/// báo cáo, không phải một câu. Một ô text gom cả danh sách vào một đoạn: mỗi mục mất đi phần "lấy số từ
/// đâu" và "gộp theo gì", và bước sinh spec phải tự đoán lại cả hai. Bảng tách mỗi báo cáo thành một dòng
/// và bằng chứng thu về là một thao tác trên TỪNG báo cáo.
/// </para>
///
/// <para>
/// <b>Vì sao bảng này KHÔNG bày ra trống.</b> Nó là bảng thứ ba trong sáu bảng, và cùng luật với ba bảng
/// giữa: BA điền sẵn từ hội thoại, người dùng bỏ tích / sửa chữ / thêm dòng. Một bảng trống là bắt người
/// dùng nghiệp vụ tự chẻ câu chuyện của họ thành bốn cột trước khi gõ được chữ nào — khó hơn hẳn kể tự do,
/// nên thứ thu về sẽ ít hơn cả cái ô text nó thay thế. Vì vậy cổng của bảng
/// (<c>ReportMapGate</c>) đòi nhóm «Báo cáo / thống kê» đã <c>[RÕ]</c>: hội thoại phải kể xong thì bảng
/// mới có gì để điền sẵn, và việc của bảng là CHỐT LẠI cho có ranh giới, không phải đi khai thác thay.
/// </para>
///
/// <para>
/// <b>Không có cột "ai xem".</b> Đó là chủ ý, không phải thiếu sót: một báo cáo LÀ một màn hình
/// (<c>ReportMapBuilder.ReportScreens</c> gieo nó vào bảng màn hình TRƯỚC lần bày đầu của
/// bảng màn hình), nên nó đi qua bảng màn hình rồi thành DÒNG của bảng phân quyền — nơi "ai xem" đã có cột riêng kèm PHẠM VI DỮ LIỆU ("của mình"
/// / "của đơn vị" / "tất cả"). Thêm một cột vai trò ở đây là dựng danh sách vai trò thứ hai trong cùng một
/// buổi phỏng vấn, và hai danh sách lệch nhau thì không tầng nào phía sau biết tin bên nào. Đó cũng là lý
/// do bảng này đứng TRƯỚC bảng màn hình rồi bảng phân quyền chứ không phải sau.
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
    /// Số liệu lấy từ ĐỐI TƯỢNG nào — chép tên một đối tượng của bảng đối tượng đã chốt ("Đơn đăng ký",
    /// "Kế hoạch đào tạo"). Đây là mối nối giữa hai bảng, và là thứ giữ cho bước sinh spec không tự nghĩ ra
    /// một nguồn dữ liệu cho báo cáo. Rỗng = chưa chốt được lấy từ đâu.
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// GỘP / LỌC theo cái gì: kỳ báo cáo, đơn vị, trạng thái, người phụ trách… Đây là cột phân biệt một báo
    /// cáo thật với một bảng đổ dữ liệu ra màn hình, và nó có đường đi riêng ở spec (điều kiện lọc thật
    /// trong "## 9. API Expectations"). Ngăn bằng dấu chấm phẩy hoặc xuống dòng.
    /// </summary>
    public string Breakdown { get; set; } = "";

    /// <summary>
    /// Ứng dụng CÓ cần báo cáo này không. BA tích sẵn ở lượt bày bảng; người dùng bỏ tích những mục BA đoán
    /// thừa. Bỏ tích chứ không xóa: dòng bị loại vẫn phải kể lại được trong tin nhắn gửi đi, nếu không
    /// người dùng không có bằng chứng nào cho thấy mình vừa loại đúng thứ định loại.
    /// </summary>
    public bool Included { get; set; } = true;

    // KHÔNG có cờ khóa-bằng-trích-dẫn ở bảng này, cùng lý do với bảng luồng và bảng màn hình (xem
    // FlowMapBuilder): mọi dòng ra khỏi Build đều ở trạng thái ĐƯỢC GIỮ, nên một trích dẫn không đổi
    // trạng thái nào mà chỉ khóa cứng dòng — đúng ở chiều duy nhất người dùng cần dùng, là bỏ tích một
    // báo cáo BA đoán thừa. Model thì luôn kèm được trích dẫn cho mọi dòng.

    /// <summary>
    /// Dòng do CHÍNH NGƯỜI DÙNG thêm bằng nút "+ thêm báo cáo" — cùng cờ và cùng luật với
    /// <see cref="ScreenScopeRow.AddedByUser"/>: dòng BA đề xuất thì bỏ tích, dòng người dùng tự thêm mới
    /// có nút xóa, vì nó chưa bao giờ là một đề xuất nên không có gì để kể lại.
    /// </summary>
    public bool AddedByUser { get; set; }
}
