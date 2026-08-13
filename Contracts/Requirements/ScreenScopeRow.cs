namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// MỘT dòng của "bảng màn hình": một màn hình/tính năng dự kiến của ứng dụng, việc nó làm, các chức năng
/// trên đó, và các BƯỚC LUỒNG mà nó phục vụ.
///
/// <para>
/// Vì sao bảng này tồn tại, và vì sao nó phải đứng TRƯỚC bảng phân quyền: các DÒNG của bảng phân quyền lấy
/// từ <c>Project.PlannedScope</c> — một danh sách do LLM chắt ra sau mỗi lượt chat mà người dùng KHÔNG bao
/// giờ nhìn thấy (panel sidebar hiển thị nó đã bị gỡ). Nghĩa là toàn bộ phần phân quyền, thứ đã được dựng
/// cẩn thận để có bằng chứng trên từng ô, lại đang đứng trên một danh sách màn hình chưa ai duyệt: một màn
/// hình LLM chắt nhầm sẽ được người dùng tích quyền cho, và một màn hình bị bỏ quên thì không bao giờ có
/// mặt để họ phản đối.
/// </para>
///
/// <para>
/// <see cref="FlowSteps"/> là phần trả tiền nhiều nhất của dòng này. Nó cho một phép kiểm TẤT ĐỊNH mà
/// không cần lời gọi LLM nào: mọi bước của bảng luồng đã chốt phải được ít nhất một màn hình phụ trách, và
/// một màn hình không phục vụ bước nào là dấu hiệu hoặc nó thừa, hoặc còn một luồng chưa được hỏi. Cả hai
/// đều là lỗi mà đọc riêng từng danh sách không thấy được.
/// </para>
/// </summary>
public class ScreenScopeRow
{
    /// <summary>
    /// Tên màn hình. Bản chuẩn hoá luôn lấy lại đúng chữ của danh sách cho phép chứ không lấy chữ của model
    /// — cùng luật với <see cref="PermissionMatrixRow.Screen"/> và với bảng cột, và cùng lý do: một dòng bịa
    /// lọt qua là một tính năng ngoài phạm vi đi vào tài liệu mang chữ ký người dùng. Danh sách cho phép là
    /// <c>Project.PlannedScope</c> ở lượt BÀY BẢNG, nhưng là chính bảng server đã render ở đường GỬI — xem
    /// <c>ScreenScopeMapBuilder.Sanitize</c>.
    /// </summary>
    public string Screen { get; set; } = "";

    /// <summary>
    /// Màn hình này để làm gì, một câu theo góc nhìn nghiệp vụ. BA ĐIỀN SẴN — một bảng ô trống là bắt người
    /// dùng nghiệp vụ tự viết đặc tả cho mười mấy màn hình, đúng thái cực mà bảng cột đã cấm.
    /// </summary>
    public string Purpose { get; set; } = "";

    /// <summary>
    /// Các chức năng chính trên màn hình, viết liền một dòng ngăn bằng dấu phẩy ("Xem danh sách, Tạo mới,
    /// Duyệt"). Cố ý là MỘT ô text sửa được chứ không phải một danh sách con: đây là bảng để rà PHẠM VI,
    /// còn quyền của từng chức năng theo từng vai là việc của bảng phân quyền ngay sau đó — tách hai việc
    /// ra là điều kiện để cả hai bảng còn đọc được trên một màn hình.
    /// </summary>
    public string Functions { get; set; } = "";

    /// <summary>
    /// Các BƯỚC của bảng luồng mà màn hình này phục vụ, mỗi bước một mục (chép phần <c>action</c> của bước).
    /// Rỗng là hợp lệ với màn hình tra cứu/báo cáo không nằm trong luồng nào — nhưng khi bảng luồng đã chốt
    /// mà một bước không được màn hình nào nhắc tới thì UI nói thẳng ra, xem <c>ScreenScopeMapBuilder</c>.
    /// </summary>
    public List<string> FlowSteps { get; set; } = new();

    /// <summary>
    /// Màn hình này có thuộc ứng dụng không. BA TÍCH SẴN theo đề xuất của mình; người dùng bỏ tích thứ họ
    /// không cần. Bỏ tích chứ không xóa: dòng bị loại vẫn phải kể lại được trong tin nhắn gửi đi, nếu không
    /// người dùng không có bằng chứng nào cho thấy mình vừa loại đúng thứ định loại.
    /// </summary>
    public bool Included { get; set; } = true;

    /// <summary>
    /// Dòng có BẰNG CHỨNG trong hội thoại ⇒ khóa (hiện ✓ + tooltip trích dẫn). Cùng luật bằng chứng với
    /// <see cref="PermissionGrant.Locked"/>: server không nhận lời tuyên bố "người dùng đã nói điều này" từ
    /// một lá cờ, phải có trích dẫn đi kèm.
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>Trích dẫn ngắn từ hội thoại — chỉ có nghĩa khi <see cref="Locked"/>.</summary>
    public string Evidence { get; set; } = "";
}
