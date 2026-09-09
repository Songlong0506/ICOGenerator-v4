namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Khoá của các khối HƯỚNG DẪN đặt trên/dưới năm bảng của buổi phỏng vấn — đoạn văn nói cho người dùng
/// biết bảng đang bày ra cái gì và sửa nó thế nào (<c>.permmap-howto</c>), cùng câu gợi ý dưới thanh nút
/// (<c>.permmap-hint</c>).
///
/// <para>
/// <b>Vì sao chúng cần một danh sách khoá thay vì cứ viết thẳng vào markup.</b> Các bảng này được vẽ HAI
/// lần — Razor lúc tải trang / sau F5, <c>requirements.js</c> lúc dựng lại ở frame <c>done</c> — nên mỗi
/// đoạn văn ấy trước đây tồn tại hai bản chép tay giống hệt nhau, và bản chép thì không có gì bắt nó đổi
/// theo. Khác với nhãn nút (đi qua <see cref="RequirementScreenText"/> được vì chỉ là chữ trần), đoạn văn
/// ở đây DÍNH MARKUP: mỗi đoạn có vài thẻ <c>&lt;b&gt;</c> nhấn đúng chỗ người dùng cần nhìn, nên nhét
/// chúng vào một hằng số chuỗi chỉ đổi một chỗ chép thành một chỗ chép khó đọc hơn.
/// </para>
///
/// <para>
/// <b>Cách gom.</b> Bản DUY NHẤT của mỗi đoạn nằm ở <c>Views/Requirements/_TableGuide.cshtml</c>. Server
/// render nó hai chỗ: thẳng vào panel (để lần tải trang đầu có sẵn chữ, không chờ JS), và một lần nữa vào
/// <c>&lt;template id="guide-&lt;khoá&gt;"&gt;</c> để <c>requirements.js</c> đọc lại từ DOM khi dựng bảng —
/// cùng lối "đọc lại nhãn từ chính DOM" mà <c>poc-review.js</c> và <c>agent-dashboard.js</c> đang dùng.
/// Chữ vì thế chỉ tồn tại MỘT bản trong repo, dù trang vẫn in ra hai bản.
/// </para>
///
/// <para>
/// <b>Khoá sai thì hỏng TO chứ không hỏng thầm.</b> Partial ném <see cref="ArgumentOutOfRangeException"/>
/// cho khoá lạ, và <c>RequirementTableGuideTests</c> chốt ba chiều: mọi khoá JS hỏi đều có ở đây, mọi khoá
/// ở đây đều có một nhánh trong partial, và không bên nào được khai lại hai lớp div ấy nữa.
/// </para>
/// </summary>
public static class RequirementTableGuide
{
    /// <summary>Bảng vai trò (các CỘT của bảng phân quyền).</summary>
    public const string PermRoles = "permRoles";

    /// <summary>Bảng phân quyền.</summary>
    public const string PermMatrix = "permMatrix";

    /// <summary>Câu gợi ý dưới bảng phân quyền.</summary>
    public const string PermMatrixHint = "permMatrixHint";

    /// <summary>Bảng luồng nghiệp vụ.</summary>
    public const string FlowMap = "flowMap";

    /// <summary>Câu gợi ý dưới bảng luồng nghiệp vụ.</summary>
    public const string FlowMapHint = "flowMapHint";

    /// <summary>Bảng màn hình &amp; chức năng.</summary>
    public const string ScreenScope = "screenScope";

    /// <summary>Câu gợi ý dưới bảng màn hình.</summary>
    public const string ScreenScopeHint = "screenScopeHint";

    /// <summary>Bảng đối tượng nghiệp vụ.</summary>
    public const string EntityMap = "entityMap";

    /// <summary>Câu gợi ý dưới bảng đối tượng.</summary>
    public const string EntityMapHint = "entityMapHint";

    /// <summary>Bảng báo cáo / thống kê.</summary>
    public const string ReportMap = "reportMap";

    /// <summary>Câu gợi ý dưới bảng báo cáo.</summary>
    public const string ReportMapHint = "reportMapHint";

    /// <summary>Bảng danh sách người nhận.</summary>
    public const string NotifRecipients = "notifRecipients";

    /// <summary>Bảng thông báo / nhắc nhở.</summary>
    public const string NotificationMap = "notificationMap";

    /// <summary>Câu gợi ý dưới bảng thông báo.</summary>
    public const string NotificationMapHint = "notificationMapHint";

    /// <summary>
    /// Mọi khoá, theo thứ tự bày trong buổi phỏng vấn. <c>Index.cshtml</c> lặp trên danh sách này để in ra
    /// các thẻ <c>&lt;template&gt;</c> — thêm một khối hướng dẫn mới thì chỉ cần thêm hằng số ở đây và một
    /// nhánh trong partial, không phải sờ vào chỗ in template.
    /// </summary>
    public static readonly string[] Keys =
    {
        PermRoles, PermMatrix, PermMatrixHint,
        FlowMap, FlowMapHint,
        ScreenScope, ScreenScopeHint,
        EntityMap, EntityMapHint,
        ReportMap, ReportMapHint,
        NotifRecipients, NotificationMap, NotificationMapHint
    };
}
