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
/// này và gieo chúng vào bảng màn hình ở trạng thái chờ duyệt. Người dùng rà chúng một lần, ở bảng màn
/// hình — bày thêm một bảng báo cáo để "chốt lại" là hỏi ba lần cho một danh sách.
/// </para>
///
/// <para>
/// <b>Báo cáo đứng ở đâu.</b> Mặc định là một CHỨC NĂNG của màn tổng quan chung (<see cref="Screen"/>), để
/// người dùng có một cái nhìn overview thay vì mở từng mục menu; chỉ thành màn hình riêng khi
/// <see cref="OwnScreen"/>. Lý do đầy đủ ở <c>ReportMapBuilder</c>.
/// </para>
///
/// <para>
/// <b>Không có cột "ai xem".</b> Đó là chủ ý, không phải thiếu sót: màn hình mà báo cáo đứng trên
/// (<c>ReportMapBuilder.ReportScreenAdditions</c> gieo nó vào bảng màn hình TRƯỚC lần bày đầu của bảng ấy)
/// đi qua bảng màn hình rồi thành DÒNG của bảng phân quyền — nơi "ai xem" đã có cột riêng kèm PHẠM
/// VI DỮ LIỆU ("của mình" / "của đơn vị" / "tất cả"). Thêm một cột vai trò ở đây là dựng danh sách vai trò
/// thứ hai trong cùng một buổi phỏng vấn, và hai danh sách lệch nhau thì không tầng nào phía sau biết tin
/// bên nào.
/// </para>
/// </summary>
public class ReportMapRow
{
    /// <summary>
    /// Tên báo cáo, tự nói ra nó đếm/liệt kê cái gì ("Remaining Leave Report") — vì nó sẽ thành một mục
    /// phạm vi thật: tên một chức năng của màn tổng quan, hoặc tên màn hình riêng. Một cái tên trống nghĩa
    /// ("Thống kê") thì tới bảng màn hình không ai rà nổi nó.
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
    /// Báo cáo này cần MÀN HÌNH RIÊNG thay vì nằm trên màn tổng quan. Model khai, và chỉ được khai khi có
    /// một trong hai lý do: người xem / phạm vi dữ liệu của nó KHÁC các báo cáo còn lại (quyền chốt theo
    /// màn hình, nên gom chung là mất khả năng phân quyền riêng), hoặc nó là một bảng chi tiết theo kỳ, có
    /// bộ lọc riêng và để xuất file — nhét vào màn tổng quan thì màn đó thành một dãy bộ lọc không liên
    /// quan nhau. <c>ReportMapBuilder.Build</c> hạ cờ này khi <see cref="FilteredList"/>.
    /// </summary>
    public bool OwnScreen { get; set; }

    /// <summary>
    /// Báo cáo này thực chất là DANH SÁCH BẢN GHI LỌC theo một điều kiện ("các tủ đang Damaged", "ticket
    /// đang chờ duyệt") — thứ mà một màn hình làm việc của luồng đã hiển thị. Trên màn tổng quan nó chỉ là
    /// một ô đếm, bấm vào thì mở màn hình làm việc đó đã lọc sẵn; dựng lại danh sách là có hai màn hình
    /// cho cùng một việc.
    /// </summary>
    public bool FilteredList { get; set; }

    /// <summary>
    /// Tên MÀN HÌNH mà báo cáo này đứng trên: màn tổng quan chung, hoặc màn riêng của nó khi
    /// <see cref="OwnScreen"/>. Do <c>ReportMapBuilder.Build</c> gán — chữ model điền vào đây bị bỏ. Rỗng =
    /// dòng ráp từ hồi mỗi báo cáo còn là một màn hình: nó đứng trên màn riêng mang tên nó.
    /// </summary>
    public string Screen { get; set; } = "";

    /// <summary>
    /// Ứng dụng CÓ cần báo cáo này không. Mọi dòng ráp ra đều được giữ; cờ này chỉ còn <c>false</c> ở dữ
    /// liệu của dự án cũ, thời bảng màn hình còn bày ra cho người dùng bỏ tích báo cáo. Hạ cờ chứ không xóa
    /// dòng: báo cáo bị loại vẫn phải được kể ra trong khối ngữ cảnh, nếu không BA đề xuất lại nó.
    /// </summary>
    public bool Included { get; set; } = true;
}
