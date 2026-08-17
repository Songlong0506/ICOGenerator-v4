namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// NGƯỜI NHẬN một thông báo — vế mà một danh sách vai trò phẳng KHÔNG chở được.
///
/// <para>
/// Đây là chốt chặn của cả bảng thông báo. Câu trả lời thật của người dùng gần như luôn là một người có
/// QUAN HỆ với bản ghi ("báo cho người gửi đơn", "báo cho sếp của người đó"), chứ không phải một vai trò:
/// "Nhân viên" hiểu theo nghĩa vai trò là TOÀN BỘ nhân viên nhà máy. Ca thật đã ghi lại ở
/// <c>requirement-coverage.v3.md</c>: BA hỏi "vai trò nào cần nhận email?", người dùng bấm bốn chip vai
/// trò, và tài liệu đóng băng thành "mọi thay đổi trạng thái gửi cho cả bốn nhóm" — mỗi lần một bản kế
/// hoạch đổi trạng thái là cả nhà máy nhận email. Không ai nói thế, và không cổng nào bắt được nữa.
/// </para>
///
/// <para>
/// Nên danh sách chọn có HAI PHẦN, và phần nguy hiểm được GỌI ĐÚNG TÊN: bốn mục quan hệ ở đây (ca thường
/// gặp), rồi mỗi vai trò đã chốt thành một mục "<c>Toàn bộ &lt;vai&gt;</c>". Chữ "toàn bộ" nằm ngay trên
/// mặt tùy chọn, nên chọn nó là một quyết định có ý thức chứ không phải một cú bấm cho xong — đó là toàn
/// bộ khác biệt giữa bảng này và hàng chip nó thay thế.
/// </para>
/// </summary>
public static class NotificationRecipient
{
    /// <summary>Người lập/gửi bản ghi.</summary>
    public const string Creator = "Người tạo";

    /// <summary>Người đứng tên xử lý bản ghi (người được giao việc, người được phân công).</summary>
    public const string Assignee = "Người được phân công";

    /// <summary>Quản lý trực tiếp của người tạo — "sếp của người gửi đơn".</summary>
    public const string CreatorManager = "Quản lý trực tiếp của người tạo";

    /// <summary>Trưởng đơn vị liên quan tới bản ghi (HOD của phòng/ban đứng tên).</summary>
    public const string OrgUnitHead = "HOD của đơn vị liên quan";

    /// <summary>Bốn mục QUAN HỆ, luôn đứng đầu danh sách chọn vì đây là ca thường gặp.</summary>
    public static readonly string[] Related = { Creator, Assignee, CreatorManager, OrgUnitHead };

    /// <summary>Tiền tố của mục "cả vai trò" — xem ghi chú class.</summary>
    public const string AllOfRolePrefix = "Toàn bộ ";

    /// <summary>Mục "gửi cho MỌI người mang vai này".</summary>
    public static string AllOfRole(string role) => AllOfRolePrefix + (role ?? string.Empty).Trim();
}

/// <summary>
/// MỘT dòng của "bảng thông báo / nhắc nhở": một sự kiện, và ai được gửi email khi nó xảy ra.
///
/// <para>
/// <b>Vì sao bảng này đứng CUỐI CÙNG, sau cả bảng phân quyền.</b> Hai cột của nó vay từ hai bảng trước:
/// các DÒNG là chuyển trạng thái của bảng đối tượng đã chốt, còn danh sách người nhận cần các VAI TRÒ của
/// bảng phân quyền đã chốt (<c>PermissionMatrixBuilder.Roles</c>) — vai trò của ứng dụng đang thiết kế chỉ
/// tồn tại trong hội thoại, không có bảng nào trong DB liệt kê chúng. Hỏi sớm hơn là bày ra một bảng mà
/// chính BA cũng chưa biết đủ dòng lẫn đủ cột để điền sẵn.
/// </para>
///
/// <para>
/// <b>Vì sao là bảng chứ không phải câu hỏi.</b> Nhóm «Thông báo / nhắc nhở» đòi hai vế GHÉP ĐƯỢC với
/// nhau — mỗi loại sự kiện biết ai là người nhận của RIÊNG nó. Hỏi bằng chip thì hai vế đó nằm ở hai câu
/// rời nhau ("vai trò nào cần nhận email?" + "sự kiện nào cần gửi?") và không gì nối chúng lại; người dùng
/// bấm bốn chip mỗi câu là nhóm lên <c>[RÕ]</c> với nội dung "mọi sự kiện gửi cho mọi người". Bảng ghép
/// sẵn hai vế trên cùng một dòng, và bằng chứng thu về là một thao tác trên TỪNG sự kiện.
/// </para>
///
/// <para>
/// Kênh gửi KHÔNG phải một cột: hằng số nền tảng của sản phẩm đã chốt chỉ có email
/// (<c>organization-platform.v1.md</c>). Thêm cột kênh là mời người dùng chọn một thứ không tồn tại.
/// </para>
/// </summary>
public class NotificationMapRow
{
    /// <summary>
    /// Đối tượng nghiệp vụ chở sự kiện ("Đơn đăng ký", "Kế hoạch đào tạo") — lấy từ bảng đối tượng đã chốt.
    /// Dùng để nhóm các dòng trên bảng và để nối một dòng về đúng chuyển trạng thái của nó. Rỗng với dòng
    /// NHẮC NHỞ không gắn vào đối tượng nào.
    /// </summary>
    public string Entity { get; set; } = "";

    /// <summary>
    /// Sự kiện làm thông báo được gửi. Với dòng gieo từ bảng đối tượng, đây là TÊN TRẠNG THÁI đối tượng vừa
    /// chuyển vào ("Đã duyệt", "Bị từ chối") — chép đúng chữ của bảng đó, vì hai bảng phải nối được với nhau
    /// ở bước sinh spec.
    /// </summary>
    public string Event { get; set; } = "";

    /// <summary>
    /// Khi nào sự kiện xảy ra — điều kiện/hành động, chép từ ô "khi nào chuyển vào" của bảng đối tượng
    /// ("HOD bấm duyệt"). Chỉ để đọc: nó là thứ giúp người dùng nhận ra dòng này nói về cái gì.
    /// </summary>
    public string Trigger { get; set; } = "";

    /// <summary>
    /// Người nhận CHÍNH. Mỗi phần tử phải là một mục của danh sách chọn
    /// (<c>NotificationMapBuilder.RecipientOptions</c>) — xem <see cref="NotificationRecipient"/>.
    ///
    /// <para>
    /// Rỗng KHÔNG có nghĩa "chưa hỏi": nó có nghĩa gì thì do <see cref="Needed"/> quyết định. Bỏ tích
    /// <see cref="Needed"/> = người dùng nói KHÔNG gửi thông báo ở sự kiện này (một quyết định hợp lệ và
    /// phải nói ra được, vì mặc định im lặng của các tầng sau là gửi cho tất cả). Còn tích mà To rỗng =
    /// "cần báo nhưng chưa chốt được ai" — đó là chỗ DUY NHẤT BA còn được phép hỏi lại sau khi bảng đã chốt.
    /// </para>
    /// </summary>
    public List<string> To { get; set; } = new();

    /// <summary>Người nhận ĐỒNG GỬI. Cùng luật với <see cref="To"/>; rỗng là ca thường gặp.</summary>
    public List<string> Cc { get; set; } = new();

    /// <summary>
    /// Sự kiện này CÓ gửi thông báo không. BA tích sẵn ở lượt bày bảng (bất kể model trả gì — xem
    /// <c>NotificationMapBuilder.Build</c>); người dùng bỏ tích những sự kiện không cần báo.
    /// </summary>
    public bool Needed { get; set; } = true;

    /// <summary>Dòng có BẰNG CHỨNG trong hội thoại ⇒ khóa. Cùng luật với các bảng khác.</summary>
    public bool Locked { get; set; }

    /// <summary>Trích dẫn ngắn từ hội thoại — chỉ có nghĩa khi <see cref="Locked"/>.</summary>
    public string Evidence { get; set; } = "";

    /// <summary>
    /// Dòng do CHÍNH NGƯỜI DÙNG thêm bằng nút "+ thêm sự kiện", không phải dòng gieo từ bảng đối tượng —
    /// cùng cờ và cùng luật với <see cref="ScreenScopeRow.AddedByUser"/> và <see cref="EntityMapRow.AddedByUser"/>.
    ///
    /// <para>
    /// Nút đó tồn tại vì nửa "NHẮC NHỞ" của nhóm không phải chuyển trạng thái: "trước hạn 3 ngày", "quá hạn
    /// mà chưa ai duyệt" không có dòng nào trong bảng đối tượng để gieo ra. Không có chỗ tự thêm thì nửa ấy
    /// biến mất trong im lặng ngay tại cái bảng sinh ra để chốt nó.
    /// </para>
    ///
    /// <para>
    /// Cờ chỉ được đọc ở đường GỬI (<c>NotificationMapBuilder.Sanitize</c>): ở lượt bày bảng mọi dòng đều do
    /// cơ chế/model soạn, nên đọc cờ ở đó là dựng cho model một cửa sau đi vòng chốt chặn "dòng bịa" bằng
    /// cách tự khai là người dùng.
    /// </para>
    /// </summary>
    public bool AddedByUser { get; set; }
}
