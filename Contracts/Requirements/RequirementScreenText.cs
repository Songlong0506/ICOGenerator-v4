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
    public const string LlmFailurePrefix = "⚠️ Lời gọi AI thất bại";

    /// <summary>Câu báo lỗi khi một lượt chat hỏng — server trả trong frame <c>done</c>, JS dùng khi khung
    /// <c>error</c> rỗng.</summary>
    public const string ChatTurnFailed = "Có lỗi khi xử lý lượt chat. Vui lòng thử lại.";

    /// <summary>Nhãn trợ năng của ô "Ý khác" trong thẻ hỏi gộp.</summary>
    public const string OtherAnswerLabel = "Ý khác — câu trả lời anh/chị tự nhập";

    /// <summary>Placeholder của ô "Ý khác" trong thẻ hỏi gộp.</summary>
    public const string OtherAnswerPlaceholder = "Không gợi ý nào đúng, hoặc muốn nói thêm? Anh/chị gõ vào đây…";

    /// <summary>Placeholder ô trả lời của một câu hỏi MỞ trong thẻ hỏi gộp.</summary>
    public const string OpenAnswerPlaceholder = "Anh/chị kể giúp mình, càng chi tiết càng tốt…";

    /// <summary>Nhãn nút gửi thẻ hỏi gộp khi chưa có câu nào được trả lời.</summary>
    public const string BatchNoAnswerYet = "Chưa trả lời câu nào";

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

    /// <summary>Nhãn nút "✎ Gửi ghi chú cho BA sửa" dưới bản mô tả sản phẩm.</summary>
    public const string SendNotesToBa = "✎ Gửi ghi chú cho BA sửa";

    /// <summary>Câu báo lỗi khi gửi ghi chú thất bại — server trả về, JS dùng khi khung lỗi rỗng.</summary>
    public const string NoteSendFailed = "Không gửi được ghi chú.";

    // ---------------------------------------------------------------- ô nhập của năm bảng
    //
    // Placeholder và nhãn trợ năng của các ô trong bảng luồng / màn hình / đối tượng / báo cáo / thông báo
    // / phân quyền. Đây đúng là lớp chữ mà cả Razor lẫn requirements.js CÙNG viết ra trên cùng một phần tử
    // <textarea>/<input>, nên bản chép ở đây trôi lệch là hai đường render bày ra hai ô khác chữ nhau.

    /// <summary>Nhãn trợ năng ô tên vai trò (bảng phân quyền).</summary>
    public const string RoleName = "Tên vai trò";

    /// <summary>Placeholder ô điều kiện của một dòng bảng phân quyền.</summary>
    public const string PermissionCondition = "vd: chỉ sửa khi chưa submit";

    /// <summary>Tiền tố tooltip nút ↑ của một bước luồng — hai bên nối thêm tên bước vào sau.</summary>
    public const string MoveStepUp = "Đưa lên trên";

    /// <summary>Tiền tố tooltip nút ↓ của một bước luồng — hai bên nối thêm tên bước vào sau.</summary>
    public const string MoveStepDown = "Đưa xuống dưới";

    /// <summary>Tiền tố nhãn trợ năng nút thêm bước — hai bên nối thêm tên luồng vào sau.</summary>
    public const string AddStepForFlow = "Thêm bước cho luồng";

    /// <summary>Placeholder ô "ai làm" của một bước luồng.</summary>
    public const string FlowActor = "ai làm bước này?";

    /// <summary>Placeholder ô "làm gì" của một bước luồng.</summary>
    public const string FlowAction = "bước này làm gì?";

    /// <summary>Placeholder ô "sau đó" của một bước luồng.</summary>
    public const string FlowOutcome = "trạng thái sau bước (nếu có)";

    /// <summary>Nhãn nút thêm một bước vào cuối luồng.</summary>
    public const string AddStepLabel = "+ thêm bước";

    /// <summary>Nhãn nút thêm một luồng vào cuối bảng (tooltip là <see cref="AddFlow"/>).</summary>
    public const string AddFlowLabel = "+ thêm luồng";

    /// <summary>Placeholder ô tên chức năng (bảng màn hình).</summary>
    public const string ScreenFunction = "chức năng";

    /// <summary>Placeholder ô bước luồng mà một chức năng phụ trách.</summary>
    public const string ScreenFunctionSteps = "chức năng này phụ trách bước nào?";

    /// <summary>Placeholder ô mục đích của một màn hình.</summary>
    public const string ScreenPurpose = "màn này để làm gì?";

    /// <summary>Nhãn cột/ô chọn nguồn danh sách của một trường (bảng đối tượng).</summary>
    public const string EntityFieldSource = "Danh sách lấy ở đâu";

    /// <summary>Placeholder ô tên một thông tin cần lưu.</summary>
    public const string EntityFieldName = "thông tin cần lưu";

    /// <summary>Placeholder ô ý nghĩa của một thông tin.</summary>
    public const string EntityFieldMeaning = "thông tin này là gì?";

    /// <summary>Placeholder ô tên một trạng thái trong vòng đời đối tượng.</summary>
    public const string EntityStateName = "tên trạng thái";

    /// <summary>Placeholder ô điều kiện vào một trạng thái.</summary>
    public const string EntityStateEntry = "điều kiện/hành động đưa vào trạng thái này";

    /// <summary>Placeholder ô mô tả một đối tượng nghiệp vụ.</summary>
    public const string EntityDescription = "đối tượng này là gì?";

    /// <summary>Nhãn trợ năng ô tên báo cáo.</summary>
    public const string ReportName = "Tên báo cáo";

    /// <summary>Placeholder ô tên báo cáo.</summary>
    public const string ReportNamePlaceholder = "tên báo cáo…";

    /// <summary>Nhãn trợ năng ô câu hỏi mà báo cáo trả lời.</summary>
    public const string ReportQuestion = "Báo cáo này trả lời câu hỏi gì";

    /// <summary>Placeholder ô câu hỏi mà báo cáo trả lời.</summary>
    public const string ReportQuestionPlaceholder = "để biết điều gì?";

    /// <summary>Nhãn trợ năng ô gộp/lọc của một báo cáo.</summary>
    public const string ReportBreakdown = "Gộp hoặc lọc theo";

    /// <summary>Placeholder ô gộp/lọc của một báo cáo.</summary>
    public const string ReportBreakdownPlaceholder = "kỳ, đơn vị, trạng thái…";

    /// <summary>Nhãn trợ năng ô tên một người nhận (bảng danh sách người nhận).</summary>
    public const string RecipientName = "Tên người nhận";

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

        // Tên người nhận theo QUAN HỆ (NotificationRecipient): giá trị nghiệp vụ đi thẳng vào JSON đã lưu
        // của bảng thông báo, và câu gợi ý dưới bảng gọi tên hai mục đầu — hai bên render đều lấy từ đây.
        ["recipients"] = new Dictionary<string, string>
        {
            ["creator"] = NotificationRecipient.Creator,
            ["creatorManager"] = NotificationRecipient.CreatorManager
        },

        ["labels"] = new Dictionary<string, string>
        {
            ["editLastTurn"] = EditLastTurn,
            ["retryFailedTurn"] = RetryFailedTurn,
            ["batchQuestionsLead"] = BatchQuestionsLead,
            ["llmFailurePrefix"] = LlmFailurePrefix,
            ["chatTurnFailed"] = ChatTurnFailed,
            ["otherAnswerLabel"] = OtherAnswerLabel,
            ["otherAnswerPlaceholder"] = OtherAnswerPlaceholder,
            ["openAnswerPlaceholder"] = OpenAnswerPlaceholder,
            ["batchNoAnswerYet"] = BatchNoAnswerYet,
            ["addFlow"] = AddFlow,
            ["deleteRow"] = DeleteRow,
            ["deleteState"] = DeleteState,
            ["deleteRole"] = DeleteRole,
            ["deleteRecipient"] = DeleteRecipient,
            ["pickRecipients"] = PickRecipients,
            ["noCcRecipients"] = NoCcRecipients,
            ["reportSourceEntity"] = ReportSourceEntity,
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
            ["screenPurpose"] = ScreenPurpose,
            ["entityFieldSource"] = EntityFieldSource,
            ["entityFieldName"] = EntityFieldName,
            ["entityFieldMeaning"] = EntityFieldMeaning,
            ["entityStateName"] = EntityStateName,
            ["entityStateEntry"] = EntityStateEntry,
            ["entityDescription"] = EntityDescription,
            ["reportName"] = ReportName,
            ["reportNamePlaceholder"] = ReportNamePlaceholder,
            ["reportQuestion"] = ReportQuestion,
            ["reportQuestionPlaceholder"] = ReportQuestionPlaceholder,
            ["reportBreakdown"] = ReportBreakdown,
            ["reportBreakdownPlaceholder"] = ReportBreakdownPlaceholder,
            ["recipientName"] = RecipientName
        }
    };
}
