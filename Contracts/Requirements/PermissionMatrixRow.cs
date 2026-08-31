namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// PHẠM VI DỮ LIỆU của một quyền — vế mà một ô tích/không-tích KHÔNG chở được.
///
/// <para>
/// Ma trận phân quyền vẽ trên giấy thường là bảng nhị phân (vai trò × chức năng, đánh dấu ✓). Đem nguyên
/// hình dạng đó vào đây là làm NGHÈO đi so với chính khung chat: quyết định thật của người dùng gần như
/// luôn có mệnh đề phạm vi — "Assistant xem và chỉnh các Training Plan <b>do mình lập</b>", "manager xem
/// ticket <b>của nhân viên thuộc quyền</b>". Một dấu tích chỉ ghi được "có xem", và bước soạn tài liệu
/// buộc phải tự đoán xem là xem của ai — đúng chỗ trống mà cả tính năng này sinh ra để lấp.
/// </para>
///
/// <para>
/// Bốn giá trị là bốn nấc phạm vi mà người dùng nghiệp vụ phân biệt được không cần giải thích. Chúng được
/// lưu dưới dạng CHUỖI (không phải enum) vì cùng lý do với các enum khác của repo: chuỗi đã nằm trong JSON
/// của <c>Project.PermissionMatrix</c> và của hội thoại, đổi tên là làm hỏng dữ liệu cũ.
/// </para>
/// </summary>
public static class PermissionScope
{
    /// <summary>Vai này KHÔNG có quyền — ô để trống trên bảng.</summary>
    public const string None = "";

    /// <summary>Chỉ bản ghi do chính người đó tạo/đứng tên.</summary>
    public const string Own = "của mình";

    /// <summary>Bản ghi của đơn vị/nhóm người đó phụ trách (quản lý xem của nhân viên thuộc quyền).</summary>
    public const string Unit = "của đơn vị";

    /// <summary>Toàn bộ bản ghi trong hệ thống.</summary>
    public const string All = "tất cả";

    /// <summary>Ba nấc CÓ quyền, theo thứ tự hẹp → rộng. Dùng để render ô chọn và để chuẩn hoá.</summary>
    public static readonly string[] Granted = { Own, Unit, All };

    /// <summary>Có quyền hay không — phép thử duy nhất cần dùng ở các tầng phía sau.</summary>
    public static bool IsGranted(string? scope) => !string.IsNullOrWhiteSpace(scope);
}

/// <summary>
/// MỘT ô của bảng phân quyền: vai trò này được làm chức năng của dòng, trong phạm vi nào.
/// </summary>
public class PermissionGrant
{
    /// <summary>Tên vai trò đúng như người dùng gọi trong hội thoại ("HR Assistant", "HOD HR", "Nhân viên").</summary>
    public string Role { get; set; } = "";

    /// <summary>Một trong các hằng số <see cref="PermissionScope"/>. Rỗng = không có quyền.</summary>
    public string Scope { get; set; } = PermissionScope.None;

    /// <summary>
    /// Ô này đã có BẰNG CHỨNG trong hội thoại (người dùng đã tự nói ra), nên nó được khóa và hiện thành
    /// dấu ✓ kèm trích dẫn thay vì một ô chọn.
    ///
    /// <para>
    /// Đây là ranh giới giữ cho bảng khỏi trở thành một cái chip "Đồng ý phương án này" phóng to. Nếu BA
    /// điền sẵn toàn bộ ô rồi người dùng bấm "Gửi bảng" trong ba giây, ta quay về đúng lỗi mà bảng sinh ra
    /// để chữa: một cú bấm trở thành bằng chứng cho hàng chục quyền mà không ai đọc. Nên chỉ ô có bằng
    /// chứng mới được điền sẵn; ô BA suy đoán để TRỐNG, và ô để trống lúc gửi được hiểu là "không có
    /// quyền" — một quyết định của người dùng, không phải một giả định của BA.
    /// </para>
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>Trích dẫn ngắn từ hội thoại — chỉ có nghĩa khi <see cref="Locked"/>. Hiện thành tooltip.</summary>
    public string Evidence { get; set; } = "";
}

/// <summary>
/// MỘT dòng của bảng phân quyền: một chức năng trên một màn hình, kèm quyền của TỪNG vai trò.
///
/// <para>
/// Vì sao bảng này nằm ở CUỐI buổi phỏng vấn chứ không rải trong hội thoại: câu hỏi "mỗi vai trò được xem
/// và làm những gì?" là câu bắt người dùng nghiệp vụ tự dựng cả ma trận trong đầu, nên nó gần như luôn
/// nhận về một câu đóng cửa ("cứ vậy đã, có gì tôi bổ sung sau") — rồi BA tự soạn phương án, người dùng bấm
/// một chip đồng ý, và cả nhóm phân quyền được chấm [RÕ] bằng đúng bốn chữ đó. Bảng đảo chiều chi phí:
/// tích vài chục ô có sẵn rẻ hơn hẳn việc kể ra cùng chừng ấy quyền, và bằng chứng thu được là một thao
/// tác trên TỪNG ô thay vì một chip trả lời thay cho tất cả.
/// </para>
///
/// <para>
/// Vì sao ở cuối mà không sớm hơn: các dòng của bảng là MÀN HÌNH đã chắt ra từ hội thoại
/// (bảng màn hình) — danh sách đó chỉ đứng yên khi phạm vi đã rõ. Hỏi sớm thì bảng thiếu
/// nửa số màn hình, mà quyền của một màn hình chưa tồn tại thì không ai trả lời được.
/// </para>
///
/// <para>
/// Cái bảng KHÔNG làm: quyền định hình LUỒNG ("HOD duyệt từng quý", "manager trực tiếp duyệt ticket") vẫn
/// phải hỏi trong hội thoại đúng lúc nó phát sinh — câu trả lời của chúng làm ĐỔI câu hỏi kế tiếp của BA,
/// nên hoãn xuống cuối là tự bịt mắt suốt cả buổi. Bảng chỉ gánh phần quyền CRUD theo màn hình, phần mà
/// hỏi lúc nào cũng cho ra cùng một câu trả lời.
/// </para>
/// </summary>
public class PermissionMatrixRow
{
    /// <summary>
    /// Màn hình/tính năng — phải khớp một mục của phạm vi màn hình đã rà
    /// (<c>ScreenScopeMapBuilder.EffectiveScreens</c>; bản chuẩn hoá luôn lấy lại đúng chữ của danh sách đó,
    /// không lấy chữ của model). Xem <c>PermissionMatrixBuilder</c>.
    /// </summary>
    public string Screen { get; set; } = "";

    /// <summary>Chức năng trên màn hình đó: "Xem", "Tạo", "Sửa", "Xóa", "Duyệt/Từ chối", "Cập nhật kết quả"…</summary>
    public string Function { get; set; } = "";

    /// <summary>
    /// Điều kiện dữ liệu ở mức DÒNG, viết bằng lời — thứ mà bốn nấc phạm vi vẫn không chở nổi: "chỉ đăng ký
    /// được khóa nằm trong danh sách bắt buộc của mình", "chỉ sửa khi chưa submit".
    ///
    /// <para>
    /// Ô này tồn tại vì một ca thật: kế hoạch mở lớp được tính từ danh sách "ai phải học khóa nào", nhưng
    /// không ai hỏi nhân viên có bị giới hạn chỉ đăng ký khóa của mình hay không ⇒ tài liệu để đăng ký mở
    /// tự do, và toàn bộ con số kế hoạch không còn liên quan gì tới người thật sự vào lớp. Loại ràng buộc
    /// đó đổi ngược lại cả luồng, nên bảng phải có chỗ cho nó thay vì để nó rơi.
    /// </para>
    /// </summary>
    public string Condition { get; set; } = "";

    /// <summary>
    /// Quyền của từng vai trò. Bản chuẩn hoá đảm bảo MỌI dòng đều có ĐỦ mọi vai trò của bảng (thiếu thì bù
    /// vào với <see cref="PermissionScope.None"/>): một vai chỉ xuất hiện ở vài dòng là một cột lỗ chỗ, và
    /// ô vắng mặt thì người dùng không bao giờ nhìn thấy để phản đối — đúng khiếm khuyết của hàng chip mà
    /// bảng sinh ra để chữa.
    /// </summary>
    public List<PermissionGrant> Grants { get; set; } = new();
}
