using ICOGenerator.Localization;
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
    public static string EditLastTurn => Loc.T("RequirementScreenText.EditLastTurn");

    /// <summary>Tooltip nút "↻ Thử lại" của một lượt trả lời đã lỗi.</summary>
    public static string RetryFailedTurn => Loc.T("RequirementScreenText.RetryFailedTurn");

    /// <summary>Tooltip nút "↻ thử lại" trên bong bóng cuối của người dùng (soạn lại câu trả lời).</summary>
    public static string RegenerateLastTurn => Loc.T("RequirementScreenText.RegenerateLastTurn");

    /// <summary>Dòng trạng thái trong lúc BA soạn lại câu trả lời cho lượt user cuối.</summary>
    public static string RegeneratingTurn => Loc.T("RequirementScreenText.RegeneratingTurn");

    /// <summary>Câu dẫn dự phòng của thẻ hỏi gộp, dùng khi model không viết câu dẫn nào.</summary>
    public static string BatchQuestionsLead => Loc.T("RequirementScreenText.BatchQuestionsLead");

    /// <summary>
    /// Tiền tố của lượt BA là THÔNG BÁO LỖI gọi AI (surface vào khung chat thay vì ném 500).
    ///
    /// <para>
    /// <b>Đây là cụm chữ đi qua nhiều ranh giới nhất của màn này, nên nó nằm ở đây chứ không ở
    /// <c>Services</c>.</b> Server ghi nó vào <c>AgentConversation.Message</c>; ba tầng server đọc lại bằng
    /// <c>StartsWith</c> (lọc transcript, bỏ qua khi rút lịch sử câu hỏi, quyết định có cho "Thử lại"), và
    /// <c>requirements.js</c> cũng <c>startsWith</c> chính nó để dựng nút Thử lại ở frame <c>done</c>. Bản
    /// chép phía JS trước đây là chuỗi viết tay — mối nối duy nhất của nó với hằng số server là một dòng
    /// chú thích, tức đổi một chữ ở đây thì nút Thử lại lặng lẽ biến mất mà không test nào đỏ. Nay nó đi
    /// theo <see cref="Vocabulary"/> nên <c>RequirementScreenVocabularyTests</c> giữ cả hai đầu.
    /// </para>
    /// </summary>
    public static string LlmFailurePrefix => Loc.T("RequirementScreenText.LlmFailurePrefix");

    /// <summary>Câu báo lỗi khi một lượt chat hỏng — server trả trong frame <c>done</c>, JS dùng khi khung
    /// <c>error</c> rỗng.</summary>
    public static string ChatTurnFailed => Loc.T("RequirementScreenText.ChatTurnFailed");

    /// <summary>Nhãn trợ năng của ô "Ý khác" trong thẻ hỏi gộp.</summary>
    public static string OtherAnswerLabel => Loc.T("RequirementScreenText.OtherAnswerLabel");

    /// <summary>Placeholder của ô "Ý khác" trong thẻ hỏi gộp.</summary>
    public static string OtherAnswerPlaceholder => Loc.T("RequirementScreenText.OtherAnswerPlaceholder");

    /// <summary>Placeholder ô trả lời của một câu hỏi MỞ trong thẻ hỏi gộp.</summary>
    public static string OpenAnswerPlaceholder => Loc.T("RequirementScreenText.OpenAnswerPlaceholder");

    /// <summary>Nhãn nút gửi thẻ hỏi gộp khi chưa có câu nào được trả lời.</summary>
    public static string BatchNoAnswerYet => Loc.T("RequirementScreenText.BatchNoAnswerYet");

    /// <summary>
    /// Chữ cuối dòng «Phân quyền theo nghiệp vụ» của panel tiến độ khi nhóm đã [RÕ] — dòng đó mở popup
    /// xem/sửa bảng phân quyền BA đã soạn.
    /// </summary>
    public static string CoverageOpenPermissions => Loc.T("RequirementScreenText.CoverageOpenPermissions");

    // ---------------------------------------------------------------- nút của các bảng

    /// <summary>Nút thêm một luồng vào bảng luồng nghiệp vụ.</summary>
    public static string AddFlow => Loc.T("RequirementScreenText.AddFlow");

    /// <summary>Nút xóa một dòng bảng (báo cáo, thông báo).</summary>
    public static string DeleteRow => Loc.T("RequirementScreenText.DeleteRow");


    /// <summary>Nút xóa một vai trò (cột) của bảng phân quyền.</summary>
    public static string DeleteRole => Loc.T("RequirementScreenText.DeleteRole");

    /// <summary>Nhãn nút "✎ Gửi ghi chú cho BA sửa" dưới bản mô tả sản phẩm.</summary>
    public static string SendNotesToBa => Loc.T("RequirementScreenText.SendNotesToBa");

    /// <summary>Câu báo lỗi khi gửi ghi chú thất bại — server trả về, JS dùng khi khung lỗi rỗng.</summary>
    public static string NoteSendFailed => Loc.T("RequirementScreenText.NoteSendFailed");

    // ---------------------------------------------------------------- ô nhập của các bảng
    //
    // Placeholder và nhãn trợ năng của các ô trong bảng luồng / màn hình / báo cáo / thông báo / phân
    // quyền. Đây đúng là lớp chữ mà cả Razor lẫn requirements.js CÙNG viết ra trên cùng một phần tử
    // <textarea>/<input>, nên bản chép ở đây trôi lệch là hai đường render bày ra hai ô khác chữ nhau.

    /// <summary>Nhãn trợ năng ô tên vai trò (bảng phân quyền).</summary>
    public static string RoleName => Loc.T("RequirementScreenText.RoleName");

    /// <summary>Placeholder ô điều kiện của một dòng bảng phân quyền.</summary>
    public static string PermissionCondition => Loc.T("RequirementScreenText.PermissionCondition");

    /// <summary>Tiền tố tooltip nút ↑ của một bước luồng — hai bên nối thêm tên bước vào sau.</summary>
    public static string MoveStepUp => Loc.T("RequirementScreenText.MoveStepUp");

    /// <summary>Tiền tố tooltip nút ↓ của một bước luồng — hai bên nối thêm tên bước vào sau.</summary>
    public static string MoveStepDown => Loc.T("RequirementScreenText.MoveStepDown");

    /// <summary>Tiền tố nhãn trợ năng nút thêm bước — hai bên nối thêm tên luồng vào sau.</summary>
    public static string AddStepForFlow => Loc.T("RequirementScreenText.AddStepForFlow");

    /// <summary>Placeholder ô "ai làm" của một bước luồng.</summary>
    public static string FlowActor => Loc.T("RequirementScreenText.FlowActor");

    /// <summary>Placeholder ô "làm gì" của một bước luồng.</summary>
    public static string FlowAction => Loc.T("RequirementScreenText.FlowAction");

    /// <summary>Placeholder ô "sau đó" của một bước luồng.</summary>
    public static string FlowOutcome => Loc.T("RequirementScreenText.FlowOutcome");

    /// <summary>Nhãn nút thêm một bước vào cuối luồng.</summary>
    public static string AddStepLabel => Loc.T("RequirementScreenText.AddStepLabel");

    /// <summary>Nhãn nút thêm một luồng vào cuối bảng (tooltip là <see cref="AddFlow"/>).</summary>
    public static string AddFlowLabel => Loc.T("RequirementScreenText.AddFlowLabel");

    /// <summary>Placeholder ô tên chức năng (bảng màn hình).</summary>
    public static string ScreenFunction => Loc.T("RequirementScreenText.ScreenFunction");

    /// <summary>Placeholder ô bước luồng mà một chức năng phụ trách.</summary>
    public static string ScreenFunctionSteps => Loc.T("RequirementScreenText.ScreenFunctionSteps");

    /// <summary>Placeholder ô mục đích của một màn hình.</summary>
    public static string ScreenPurpose => Loc.T("RequirementScreenText.ScreenPurpose");







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
        // Khoá nhóm có dòng bấm được trên panel tiến độ (mở popup bảng phân quyền) — JS nhận ra dòng đó
        // bằng khoá, không bằng nhãn.
        ["permissionsGroup"] = CoverageGroupKey.Permissions,

        // Phạm vi dữ liệu của một ô bảng phân quyền (PermissionScope) và hai loại luồng (FlowKind): hai bộ
        // giá trị NGHIỆP VỤ đi thẳng vào JSON đã lưu, nên bản JS phải là chính chúng chứ không phải bản chép.
        ["permScopes"] = PermissionScope.Granted,
        ["flowKinds"] = new Dictionary<string, string>
        {
            ["happy"] = FlowKind.Happy,
            ["exception"] = FlowKind.Exception
        },

        // Hai bộ trên là MÃ (không phụ thuộc ngôn ngữ). Chữ người dùng đọc ở ô chọn và trong tin nhắn gửi
        // bảng là NHÃN của chúng — mã → nhãn theo ngôn ngữ đang dùng.
        ["permScopeLabels"] = PermissionScope.Granted.ToDictionary(x => x, PermissionScope.Label),
        ["flowKindLabels"] = FlowKind.All.ToDictionary(x => x, x => FlowKind.Label(x)),

        ["labels"] = new Dictionary<string, string>
        {
            ["editLastTurn"] = EditLastTurn,
            ["retryFailedTurn"] = RetryFailedTurn,
            ["regenerateLastTurn"] = RegenerateLastTurn,
            ["regeneratingTurn"] = RegeneratingTurn,
            ["batchQuestionsLead"] = BatchQuestionsLead,
            ["llmFailurePrefix"] = LlmFailurePrefix,
            ["chatTurnFailed"] = ChatTurnFailed,
            ["otherAnswerLabel"] = OtherAnswerLabel,
            ["otherAnswerPlaceholder"] = OtherAnswerPlaceholder,
            ["openAnswerPlaceholder"] = OpenAnswerPlaceholder,
            ["batchNoAnswerYet"] = BatchNoAnswerYet,
            ["coverageOpenPermissions"] = CoverageOpenPermissions,
            ["addFlow"] = AddFlow,
            ["deleteRow"] = DeleteRow,
            ["deleteRole"] = DeleteRole,
            ["sendNotesToBa"] = SendNotesToBa,
            ["noteSendFailed"] = NoteSendFailed,

            ["roleName"] = RoleName,
            ["permissionCondition"] = PermissionCondition,
            ["moveStepUp"] = MoveStepUp,
            ["moveStepDown"] = MoveStepDown,
            ["addStepForFlow"] = AddStepForFlow,
            ["flowActor"] = FlowActor,
            ["flowAction"] = FlowAction,
            ["flowOutcome"] = FlowOutcome,
            ["addStepLabel"] = AddStepLabel,
            ["addFlowLabel"] = AddFlowLabel,
            ["screenFunction"] = ScreenFunction,
            ["screenFunctionSteps"] = ScreenFunctionSteps,
            ["screenPurpose"] = ScreenPurpose
        }
    };
}
