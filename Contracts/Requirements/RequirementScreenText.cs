namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Chữ và từ vựng mà màn Requirements dùng ở CẢ HAI đường render — bản server (<c>Views/Requirements/Index.cshtml</c>)
/// và bản trình duyệt (<c>wwwroot/js/requirements.js</c>) — gom về một chỗ.
///
/// <para>
/// <b>Vì sao cần chỗ này.</b> Bốn bảng của buổi phỏng vấn (luồng, đối tượng, báo cáo, thông báo, phân quyền)
/// và panel tiến độ được vẽ HAI lần bằng hai ngôn ngữ khác nhau: server vẽ lúc tải trang / sau F5, JS vẽ lại
/// ở frame <c>done</c> của lượt chat. Hai bản markup buộc phải khớp nhau, nên mọi nhãn nút, tooltip và giá
/// trị từ vựng trước đây được CHÉP sang JS — và bản chép thì không có gì bắt nó đổi theo. Ca điển hình: đổi
/// nhãn ở Razor xong, thêm một dòng bảng trong cùng phiên chat thì dòng mới hiện nhãn cũ, mà không test nào
/// đỏ. Nay server gửi khối này xuống <c>window.REQUIREMENTS_VOCAB</c> và JS không tự khai lại chữ nào nữa.
/// </para>
///
/// <para>
/// <b>Giá trị không được chứa dấu nháy kép.</b> Nhãn ở đây đi thẳng vào thuộc tính <c>title</c>/<c>aria-label</c>
/// ở cả hai bên mà không qua bước escape nào — đúng như hồi chúng còn là chuỗi viết tay trong markup.
/// </para>
///
/// <para>
/// <b>Chỉ chứa thứ THẬT SỰ hai bên cùng vẽ.</b> Chữ chỉ có ở một bên (câu dẫn của model, nhãn chỉ JS dựng,
/// prose trong Razor) thì để nguyên tại chỗ của nó: kéo vào đây là dựng một tầng gián tiếp không mua được gì.
/// </para>
/// </summary>
public static class RequirementScreenText
{
    // ---------------------------------------------------------------- khung chat

    /// <summary>Tooltip nút "✎ sửa" trên bong bóng cuối của người dùng.</summary>
    public const string EditLastTurn = "Sửa lại câu vừa gửi — BA sẽ trả lời lại từ nội dung mới";

    /// <summary>Tooltip nút "↻ Thử lại" của một lượt trả lời đã lỗi.</summary>
    public const string RetryFailedTurn = "Chạy lại lượt trả lời vừa lỗi — không cần gõ lại câu hỏi";

    /// <summary>Câu dẫn dự phòng của thẻ hỏi gộp, dùng khi model không viết câu dẫn nào.</summary>
    public const string BatchQuestionsLead = "Anh/chị trả lời giúp mình mấy điểm sau nhé.";

    // ---------------------------------------------------------------- nút của các bảng

    /// <summary>Nút thêm một luồng vào bảng luồng nghiệp vụ.</summary>
    public const string AddFlow = "Thêm một luồng mới vào bảng";

    /// <summary>Nút xóa một dòng bảng (báo cáo, thông báo).</summary>
    public const string DeleteRow = "Xóa dòng này";

    /// <summary>Nút xóa một trạng thái trong vòng đời của bảng đối tượng.</summary>
    public const string DeleteState = "Xóa trạng thái này";

    /// <summary>Nút xóa một vai trò (cột) của bảng phân quyền.</summary>
    public const string DeleteRole = "Xóa vai trò này";

    /// <summary>Nút xóa một người nhận trong bảng danh sách người nhận.</summary>
    public const string DeleteRecipient = "Xóa người nhận này";

    /// <summary>Chữ trong ô To của bảng thông báo khi chưa chọn ai.</summary>
    public const string PickRecipients = "Chọn người nhận";

    /// <summary>Chữ trong ô CC của bảng thông báo khi chưa chọn ai.</summary>
    public const string NoCcRecipients = "Không đồng gửi";

    /// <summary>Nhãn trợ năng của ô chọn đối tượng nguồn ở bảng báo cáo.</summary>
    public const string ReportSourceEntity = "Số liệu lấy từ đối tượng nào";

    // ---------------------------------------------------------------- panel "Tiến độ khai thác"

    /// <summary>Biểu tượng của trạng thái không nằm trong bảng dưới — gồm cả <see cref="CoverageStatus.NotAsked"/>.</summary>
    public const string CoverageIconUnknown = "⚪";

    /// <summary>
    /// Biểu tượng cho từng trạng thái của một dòng bản đồ. <see cref="CoverageStatus.NotAsked"/> cố ý KHÔNG
    /// có mặt: nó dùng chung ký hiệu với mọi giá trị lạ, và một dòng vòng tròn trắng đọc đúng cho cả hai.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CoverageIcons = new Dictionary<string, string>
    {
        [CoverageStatus.Clear] = "✅",
        [CoverageStatus.Partial] = "🟡",
        [CoverageStatus.NotApplicable] = "➖"
    };

    /// <summary>Biểu tượng của một dòng bản đồ — dùng ở bản server; bản JS tra cùng bảng trên.</summary>
    public static string CoverageIcon(string? status)
        => status is not null && CoverageIcons.TryGetValue(status, out var icon) ? icon : CoverageIconUnknown;

    // ---------------------------------------------------------------- khối gửi xuống trình duyệt

    /// <summary>
    /// Khối từ vựng serialize thành <c>window.REQUIREMENTS_VOCAB</c>, đặt TRƯỚC thẻ script của
    /// <c>requirements.js</c> vì file đó đọc nó ngay lúc nạp.
    /// <para>
    /// Khoá viết camelCase đúng như bên JS đọc, và viết TAY ngay cạnh giá trị: một chính sách đặt tên tự
    /// động thì đọc file này không còn biết bên kia gõ gì.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, object> Vocabulary() => new Dictionary<string, object>
    {
        // Trạng thái bản đồ bao phủ — JS lọc/đếm theo đúng bốn giá trị này (xem CoverageStatus).
        ["status"] = new Dictionary<string, string>
        {
            ["clear"] = CoverageStatus.Clear,
            ["partial"] = CoverageStatus.Partial,
            ["notAsked"] = CoverageStatus.NotAsked,
            ["notApplicable"] = CoverageStatus.NotApplicable
        },
        ["coverageIcons"] = CoverageIcons,
        ["coverageIconUnknown"] = CoverageIconUnknown,

        // Phạm vi dữ liệu của một ô bảng phân quyền (PermissionScope) và hai loại luồng (FlowKind): hai bộ
        // giá trị NGHIỆP VỤ đi thẳng vào JSON đã lưu, nên bản JS phải là chính chúng chứ không phải bản chép.
        ["permScopes"] = PermissionScope.Granted,
        ["flowKinds"] = new Dictionary<string, string>
        {
            ["happy"] = FlowKind.Happy,
            ["exception"] = FlowKind.Exception
        },

        ["labels"] = new Dictionary<string, string>
        {
            ["editLastTurn"] = EditLastTurn,
            ["retryFailedTurn"] = RetryFailedTurn,
            ["batchQuestionsLead"] = BatchQuestionsLead,
            ["addFlow"] = AddFlow,
            ["deleteRow"] = DeleteRow,
            ["deleteState"] = DeleteState,
            ["deleteRole"] = DeleteRole,
            ["deleteRecipient"] = DeleteRecipient,
            ["pickRecipients"] = PickRecipients,
            ["noCcRecipients"] = NoCcRecipients,
            ["reportSourceEntity"] = ReportSourceEntity
        }
    };
}
