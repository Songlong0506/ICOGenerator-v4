using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Toàn bộ khối lệnh (system message) mà <see cref="BAChatService"/> lắp vào ngữ cảnh một lượt chat BA.
/// Đây chỉ là VĂN BẢN dựng từ dữ liệu đã chốt sẵn — không truy vấn, không gọi LLM, không quyết định gì:
/// việc chọn khối nào cho lượt nào là của <see cref="InterviewTableGate"/> và của chính
/// <see cref="BAChatService"/>.
///
/// <para>
/// <b>Ranh giới với file prompt.</b> Mỗi khối ở đây có hai nửa và chúng ở hai chỗ khác nhau vì hai lý do
/// khác nhau. Nửa LUẬT — đặc tả từng trường của một bảng, cách viết, những gì tuyệt đối không làm — nằm ở
/// file prompt riêng của bảng đó (<c>Prompts/BusinessAnalyst/table-*.v1.md</c>): sửa được ở Prompt Studio,
/// đo được ở Prompt Evals, có <c>PromptKey</c> để lần vết phiên bản. Nửa DỮ LIỆU — danh sách người nhận —
/// dựng từ chính dữ liệu dự án
/// và phải khớp đúng với các builder đọc lại kết quả, nên nó ở lại code. Method trong file này chỉ NỐI hai
/// nửa ấy.
/// </para>
///
/// <para>
/// Trước đây nửa LUẬT nằm ở CẢ HAI chỗ — chuỗi C# trong file này và một bản thứ hai trong
/// <c>requirement-chat.v4.md</c> — và hai bản đã trôi lệch: bản C# bắt model điền <c>evidence</c> cho từng
/// bước của bảng luồng và từng dòng của bảng màn hình, hai trường KHÔNG hề tồn tại trên
/// <see cref="Contracts.Requirements.FlowStep"/> và <see cref="Contracts.Requirements.ScreenScopeRow"/> nên
/// bị bỏ lúc parse, trong khi bản prompt nói đúng rằng hai bảng đó không có trường ấy. Một việc, hai đặc
/// tả, và chỉ một trong hai đúng: đó là lý do nửa LUẬT nay chỉ còn MỘT bản.
/// </para>
///
/// <para>
/// <b>Ngoại lệ có chủ ý của ranh giới ấy:</b> <see cref="PermissionMatrixDeferred"/> và
/// <see cref="NotificationDeferred"/> là văn bản thuần LUẬT nhưng ở lại đây, vì chúng không phải một câu
/// dặn chung mà là MỘT NHÁNH trạng thái của cổng — nhánh còn lại là khối "bảng ĐÃ CHỐT" (và với nhóm
/// thông báo, khối "## LƯỢT NÀY: BÀY BẢNG …"). Prompt nền vào mọi lượt nên một bản sao ở đó chọi thẳng với hai nhánh kia, và với
/// nhóm thông báo còn vô hiệu hoá đường thoát "không có vòng đời nào thì hỏi bằng câu hỏi". Prompt nền nay
/// chỉ giữ con trỏ tới hai khối này; <c>InterviewTablePromptTests</c> giữ cho bản sao không mọc lại.
/// </para>
/// </summary>
public static class BAChatPromptBlocks
{
    // Checklist bổ sung được BA rút kinh nghiệm từ các dự án TRƯỚC (của bất kỳ ai) — bucket chung +
    // bucket PHÒNG BAN của đơn vị yêu cầu (gắn bắt buộc lúc tạo project, nên đúng ngay từ lượt đầu) —
    // nạp để hỏi kỹ hơn mà không bị nhiễu bởi bài học của phòng khác. Xem ChecklistNoteStore.
    public static string LearnedChecklist(string learnedChecklist)
        => "## Checklist bổ sung (rút kinh nghiệm từ các dự án trước — chủ động hỏi thêm các mục này nếu liên quan)\n"
            + learnedChecklist;

    // Hồ sơ người dùng (nếu có): nạp như một system message nền để BA hiểu user ngay từ lượt đầu, kể cả
    // ở dự án mới. Đây là điều tạo cảm giác "càng nói chuyện càng hiểu mình".
    public static string UserProfile(string userMemory)
        => "## Hồ sơ người dùng (đúc kết từ các lần trao đổi trước — dùng để hiểu & phục vụ đúng ý người dùng, KHÔNG nhắc lại như thể vừa được kể)\n"
            + userMemory;

    // Đính kèm bộ nhớ dài hạn (nếu có) như một system message nền — BA nhớ các lượt cũ đã lược bớt
    // mà không phải đọc lại nguyên văn.
    public static string ConversationMemory(string summary)
        => "## Bộ nhớ hội thoại (tóm tắt các lượt CŨ đã lược bớt để tiết kiệm token — dùng làm ngữ cảnh nền)\n"
            + summary;

    // CHẾ ĐỘ YÊU CẦU THAY ĐỔI (sau khi Brief đã duyệt — xem RequirementChangeMode). Bản Brief đã duyệt thay
    // cho bản đồ bao phủ + bốn bảng đã chốt: nó là bản duy nhất người dùng đã ký, và nó đã chở nội dung của
    // chúng. Heading phải khớp tên khối mà requirement-change.v1.md nhắc tới.
    public static string ApprovedBrief(string versionName, string content)
        => $"## Product Brief đã duyệt ({versionName} — mọi điều ghi dưới đây là ĐÃ CHỐT, không hỏi lại)\n"
            + content.Trim();

    public static string ScreenIndex(string screenIndex)
        => "## Các màn hình hiện có của ứng dụng (tên trên menu của bản demo — thiết kế BA soạn, không phải lời người dùng)\n"
            + screenIndex;

    // Bản đồ bao phủ (nếu có): la bàn để BA chọn câu hỏi kế tiếp — ưu tiên nhóm ★ chưa rõ, không hỏi
    // lại nhóm đã [RÕ]. Prompt requirement-chat.v4 hướng dẫn cách dùng heading này.
    // Bản đồ được LƯU dạng JSON nhưng nạp vào ngữ cảnh chat dạng 12 dòng bullet: BA đọc nó để chọn câu
    // hỏi, không để sửa nó, nên dấu ngoặc nhọn ở đây chỉ tốn token và mời model chép cú pháp JSON ra câu
    // trả lời cho người dùng. Xem CoverageMapParser.ToText.
    //
    // KHÔNG gắn câu hỏi vào (CoverageMapParser.AttachQuestions): bản đồ chở TRẠNG THÁI + điều ĐÃ ghi
    // nhận, còn phần CÒN PHẢI HỎI có khối riêng ngay dưới (OpenQuestions). Gắn ở đây thì mỗi câu hỏi
    // đang treo được in HAI lần trong cùng một ngữ cảnh — một lần làm vế "còn thiếu:" của dòng, một lần
    // làm mục của khối kia — và model đọc một mẩu hỏi thành hai việc khác nhau. Đo trên log BAChat
    // 2026-09-05: cả hai mục còn treo của buổi đó xuất hiện đúng hai lần.
    // Và bản đồ cũng KHÔNG chở đủ danh sách ấy, nên bỏ vế "còn thiếu:" ở đây không phải chuyện tiết kiệm
    // token: câu hỏi có nhóm rỗng (model đặt tên lạ, hoặc đường ghi đã chuẩn hoá về rỗng — xem
    // CoverageMapParser.IsSameGroup) KHÔNG khớp dòng nào, và câu hỏi gắn vào một dòng đã [RÕ] thì rơi
    // đúng vào lệnh "nhóm đã [RÕ]: KHÔNG hỏi lại" ngay bên trên nó. Cả hai loại chỉ tới được BA qua khối
    // OpenQuestions. Cùng một cách chia ở cả bốn chỗ nạp hai cột này cho một lời gọi model: ngữ cảnh chat
    // (đây), lượt distill (RequirementCoverageService), bước soạn Brief (ProductBriefDraftService) và bản
    // xuất hội thoại (ChatExportBuilder).
    public static string CoverageMap(string coverageMap)
        => "## Bản đồ bao phủ yêu cầu (trạng thái khai thác từng nhóm thông tin — dùng để chọn câu hỏi kế tiếp)\n"
            + "Nhóm đã [RÕ]: KHÔNG hỏi lại. Nhóm [MỘT PHẦN]: người dùng ĐÃ trả lời phần ghi trên dòng — "
            + "KHÔNG phát lại câu hỏi mở đầu của nhóm đó; phần còn treo nằm ở khối \"## Điểm cần làm rõ "
            + "còn tồn đọng\" (nếu có).\n"
            + CoverageMapParser.ToText(CoverageMapParser.Parse(coverageMap));

    // "Điểm cần làm rõ" (Project.OpenQuestions, ghi bởi RequirementCoverageService): tồn đọng các điểm còn mơ hồ/mâu thuẫn
    // chắt từ hội thoại. Bản đồ ở trên chỉ có độ phân giải theo NHÓM ("Quy tắc nghiệp vụ: MỘT PHẦN"),
    // còn danh sách này giữ ĐÚNG điểm chưa chốt ("Reference Belt đồng bộ tự động hay nhập tay?") —
    // BA mỗi lượt chỉ hỏi 1-2 câu nên phần chưa hỏi tới cần một chỗ để không rơi. Trước đây danh sách
    // này chỉ hiện thành panel cạnh chat để user tự đọc; nay nó đi thẳng vào ngữ cảnh của BA — người
    // dùng chỉ cần trò chuyện, việc "hỏi cho hết" là của BA.
    //
    // Và đây là chỗ DUY NHẤT các điểm ấy được in ra trong ngữ cảnh chat: bản đồ ở trên cố tình không
    // gắn chúng vào dòng của nó nữa (xem CoverageMap). Bỏ khối này đi là mất hẳn mọi mục có nhóm rỗng
    // hoặc gắn vào một dòng đã [RÕ] — không dòng bản đồ nào chở được chúng.
    public static string OpenQuestions(IReadOnlyList<OpenQuestionEntry> openQuestions)
        => "## Điểm cần làm rõ còn tồn đọng (chắt từ các lượt trước — hỏi cho hết trong khung chat)\n"
            + "Chọn câu hỏi kế tiếp ƯU TIÊN từ danh sách này khi nó còn mục, trước khi mở nhóm mới trong "
            + "bản đồ bao phủ. Điểm nào người dùng đã trả lời ở lượt gần đây thì coi như xong, KHÔNG hỏi lại.\n"
            // Nhãn nhóm («Vòng đời & trạng thái») KHÔNG đi kèm vào đây: nó được model điền cho
            // CoveragePendingGuard đối chiếu tất định với bản đồ, không phải cho BA đọc ra. Nhãn nhóm là
            // từ vựng nội bộ của bản đồ và prompt chat cấm ném nó vào mặt người dùng nghiệp vụ — nạp cả
            // nhãn thì nó đi thẳng vào câu hỏi kế tiếp, đúng lỗi mà CoverageDeadQuestionLoopTests đã phải
            // dựng lưới một lần. Xem InterviewOutlookParser.ToText.
            + InterviewOutlookParser.ToText(openQuestions);

    // ------------------------------------------------------------------------------------------------
    // CÁC BẢNG ĐÃ CHỐT — khối ngữ cảnh đính vào MỌI lượt sau, không phụ thuộc cổng nào đang mở. Thiếu
    // chúng thì mỗi bảng chỉ là một màn bấm đẹp: BA vẫn hỏi lại đúng thứ người dùng vừa duyệt.
    // Mỗi hằng dưới đây là dòng tiêu đề + câu lệnh; bảng thật được nối vào sau (xem
    // BAChatService.AppendConfirmedTable).
    // ------------------------------------------------------------------------------------------------

    public const string ConfirmedFlowMap =
        "## Bảng luồng nghiệp vụ người dùng ĐÃ CHỐT (tự tay rà từng bước — coi như điều đã biết)\n"
        + "KHÔNG hỏi lại thứ tự bước, ai làm bước nào, hay kết quả của bước; KHÔNG dựng yêu cầu trái với "
        + "các luồng này. Còn được phép hỏi các luồng CHƯA có trong bảng.";

    // Phạm vi màn hình là THIẾT KẾ CỦA BA, không phải lời người dùng. Khối cũ ("bảng màn hình người dùng
    // ĐÃ CHỐT … KHÔNG thêm chức năng ngoài các chức năng dưới đây") dồn BA vào đúng một lối thoát khi người
    // dùng kể thêm một thao tác sau lúc chốt — ca thật, dự án Locker: thêm trạng thái Need Clean, FCM bấm
    // "Đã clean" — là hỏi ngược *"nút Thu hồi tủ và nút Đã clean nên đặt ở màn hình nào?"*. Khối này dặn
    // điều ngược lại: hỏi NGHIỆP VỤ của nhu cầu mới, còn chỗ đặt nó là việc của lượt soạn phạm vi kế tiếp.
    public const string ConfirmedScreenScope =
        "## Phạm vi màn hình BA đã chốt (thiết kế của bạn — coi như điều đã biết)\n"
        + "KHÔNG hỏi người dùng ứng dụng cần màn hình nào, chức năng hay nút nào đặt ở màn hình nào, hay gom "
        + "chức năng ra sao — đó là thiết kế, việc của bạn. Người dùng kể thêm một nhu cầu MỚI (trạng thái "
        + "mới, thao tác mới) ⇒ hỏi NGHIỆP VỤ của nó như mọi câu khác (ai làm, khi nào, kết quả ra sao); hệ "
        + "thống tự thêm chức năng tương ứng vào phạm vi ở lượt sau.";

    public const string ConfirmedReportMap =
        "## Bảng báo cáo / thống kê của ứng dụng — ráp từ các báo cáo người dùng ĐÃ CHỌN (coi như điều đã biết)\n"
        + "KHÔNG hỏi lại ứng dụng cần báo cáo nào, mỗi báo cáo lấy số từ đâu hay gộp theo gì, và KHÔNG đề "
        + "xuất lại báo cáo người dùng đã bỏ. Các báo cáo còn giữ nằm trên màn hình mà khối này ghi (màn tổng "
        + "quan gom chung, hoặc màn riêng) — KHÔNG mời người dùng rà một \"bảng báo cáo\" nào, và KHÔNG "
        + "hứa mỗi báo cáo một màn hình.";

    // Bảng do BA soạn ngầm (PermissionMatrixDraftService) và người dùng sửa được ở popup của dòng «Phân
    // quyền theo nghiệp vụ» trên panel tiến độ — nên khối chỉ người dùng tới ĐÚNG chỗ đó khi họ muốn đổi một
    // quyền, thay vì để BA hỏi lại cả ma trận trong khung chat.
    public static string ConfirmedPermissionMatrix(string confirmedMatrix)
        => "## Bảng phân quyền của ứng dụng (BA soạn từ hội thoại, người dùng sửa được — coi như điều đã biết)\n"
            + "KHÔNG hỏi lại bất kỳ quyền nào dưới đây, KHÔNG bắt xác nhận lần nữa, và KHÔNG dựng yêu cầu "
            + "trái với bảng này. Người dùng muốn đổi một quyền ⇒ chỉ họ bấm vào mục «Phân quyền theo nghiệp "
            + "vụ» ở panel Tiến độ khai thác để sửa bảng.\n"
            + confirmedMatrix;

    // Chỉ còn ở dự án CŨ đã chốt bảng thông báo (bảng không còn bày ra nữa — xem NotificationDeferred).
    // Cấm TUYỆT ĐỐI, không ngoại lệ: bất biến của bảng (xem NotificationMapBuilder) bảo đảm mọi dòng đã lưu
    // đều trả lời xong — hoặc "không gửi", hoặc có người nhận.
    public static string ConfirmedNotificationMap(string confirmedNotifications)
        => "## Bảng thông báo người dùng ĐÃ CHỐT (tự tay chọn từng dòng — coi như điều đã biết)\n"
            + "KHÔNG hỏi lại sự kiện nào cần báo, cũng KHÔNG hỏi lại ai là người nhận — cả nhóm «Thông báo "
            + "/ nhắc nhở» đã xong.\n"
            + confirmedNotifications;

    // ------------------------------------------------------------------------------------------------
    // KHỐI "## LƯỢT NÀY:" — mỗi lượt đúng MỘT khối. Chúng loại trừ nhau vì cùng đến từ một lời gọi
    // InterviewTableGate.Select; hai khối cùng lúc là hai mệnh lệnh chọi nhau, model sẽ trả một bảng lai
    // hoặc bỏ cả hai.
    // ------------------------------------------------------------------------------------------------

    /// <summary>Khóa prompt của khối "## LƯỢT NÀY: BÀY BẢNG LUỒNG …".</summary>
    public const string FlowMapPromptKey = "BusinessAnalyst/table-flow-map.v1.md";

    /// <summary>
    /// Nhóm phân quyền chưa tới lượt soạn: cấm hỏi lẻ, nhưng nói rõ phần nào VẪN phải hỏi.
    ///
    /// <para>
    /// <b>Đây là bản DUY NHẤT của lệnh cấm, và nó ở code chứ không ở prompt nền.</b> Lệnh này là MỘT NHÁNH
    /// trạng thái của cổng, không phải một câu dặn chung: nhánh kia là khối "bảng ĐÃ CHỐT". Gộp nó vào
    /// <c>requirement-chat.v4.md</c> — vào MỌI lượt — là nói "sẽ soạn ở cuối buổi" về một bảng đã có. Vị trí đính cũng là một phần của tác dụng: khối này đứng NGAY SAU bản đồ bao
    /// phủ, tức ngay sau dòng <c>Phân quyền theo nghiệp vụ: [CHƯA HỎI]</c> mà nó phải giải độc — xem
    /// <see cref="PermissionMatrixGate"/> cho lý do một câu dặn ở đầu prompt nền không đủ.
    /// </para>
    /// </summary>
    public const string PermissionMatrixDeferred =
        "## Nhóm «Phân quyền theo nghiệp vụ» — ĐỂ CUỐI, đừng hỏi lẻ\n"
            + "KHÔNG hỏi các câu kiểu \"mỗi vai trò được xem và thao tác những gì\", \"vai X còn được làm gì "
            + "nữa không\", và KHÔNG tự soạn một phương án phân quyền rồi xin người dùng gật. Quyền xem/tạo/"
            + "sửa/xóa theo từng màn hình được hệ thống soạn thành MỘT BẢNG ở cuối buổi, khi phạm vi màn hình "
            + "đã đứng yên, suy từ chính những gì người dùng đã kể; người dùng xem và sửa bảng đó ở panel tiến "
            + "độ — hỏi bây giờ chỉ nhận về \"cứ vậy đã, có gì tôi bổ sung sau\", rồi phương án bạn tự viết "
            + "được đóng dấu bằng một chip \"Đồng ý\" và cả nhóm coi như đã rõ trong khi không ai thật sự "
            + "trả lời.\n"
            + "Vẫn PHẢI hỏi như thường: vai trò nào làm bước nào trong LUỒNG (ai gửi, ai duyệt, ai bị từ "
            + "chối thì làm gì), vì câu trả lời đó đổi luôn câu hỏi kế tiếp của bạn nên hoãn là tự bịt mắt; "
            + "và ai QUẢN LÝ từng danh mục dữ liệu — trừ orgUnit và nhân sự (đã chốt: đồng bộ từ COMPAS). Đó "
            + "là nhóm «Chức năng & luồng nghiệp vụ chính» và «Dữ liệu / danh mục chính», không phải nhóm "
            + "phân quyền.";

    public static string FlowMapTable(string rules) => rules;

    // THÔNG BÁO / NHẮC NHỞ — KHÔNG chốt ở buổi phỏng vấn. Sản phẩm người dùng nhận ở cuối buổi là một POC
    // không gửi email nào, nên hỏi ai nhận email ở sự kiện nào là đòi một quyết định họ chưa thấy tác dụng;
    // hỏi lẻ còn tệ hơn — câu hỏi tự nhiên tách "ai nhận" khỏi "khi nào gửi", và bốn chip vai trò đóng băng
    // tài liệu thành "mọi thay đổi trạng thái gửi cho cả bốn nhóm". Nhóm chỉ GHI NHẬN điều người dùng tự kể
    // (CoverageConfirmedTableGuard đặt nó về trạng thái không chặn), và người nhận được ghi là CHƯA CHỐT.
    //
    // Lệnh cấm vô điều kiện (trừ dự án cũ đã chốt bảng — khối ConfirmedNotificationMap thay chỗ), nên nó
    // không cần đường thoát: nhóm không bao giờ chặn nút "Write Requirement". Vẫn ở code chứ không ở
    // requirement-chat.v4.md vì nó là một nhánh trạng thái (đã chốt / chưa), cùng lý do với
    // PermissionMatrixDeferred; prompt nền chỉ giữ con trỏ, và InterviewTablePromptTests giữ cho bản sao
    // không mọc lại ở đó.
    public const string NotificationDeferred =
        "## Nhóm «Thông báo / nhắc nhở» — KHÔNG hỏi, chỉ ghi nhận\n"
            + "KHÔNG hỏi các câu kiểu \"vai trò nào cần nhận email?\", \"sự kiện nào cần gửi thông báo?\", "
            + "\"ai cần được CC?\", và KHÔNG tự soạn một danh sách người nhận rồi xin người dùng gật. Sản phẩm "
            + "của buổi này là một bản demo không gửi email thật; người nhận email của từng sự kiện được đội "
            + "Dev xác nhận với người dùng SAU POC. Nhóm này không bao giờ chặn nút \"Write Requirement\".\n"
            + "Người dùng TỰ kể về thông báo (\"duyệt xong thì báo cho người tạo\") ⇒ ghi nhận tự nhiên, "
            + "KHÔNG hỏi thêm cho đủ ai nhận / khi nào / ai được CC. Vẫn PHẢI hỏi như thường các TRẠNG THÁI "
            + "một đối tượng đi qua và ĐIỀU KIỆN chuyển giữa chúng (nhóm «Vòng đời & trạng thái») — đó là "
            + "luồng của ứng dụng, không phải chuyện thông báo. Cũng KHÔNG hỏi về cấu hình email/SMTP: kênh "
            + "gửi duy nhất đã chốt là email.";

    /// <summary>Dòng tiêu đề của khối chặng về đích — prompt chat trỏ tới ĐÚNG chuỗi này.</summary>
    public const string InterviewClosingHeading = "## CHẶNG VỀ ĐÍCH";

    /// <summary>
    /// Khối "chặng về đích": đính khi người dùng đã trả lời quá ngân sách lượt
    /// (<see cref="InterviewPaceBudget"/>) mà bản đồ còn nhóm hở phải đóng bằng câu hỏi. Nó đổi ƯU TIÊN
    /// của BA từ "đào cho sâu" sang "đóng cho xong", không đổi ai là người trả lời.
    ///
    /// <para>
    /// Ở lại code chứ không nằm trong <c>requirement-chat.v4.md</c> vì cùng lý do với
    /// <see cref="PermissionMatrixDeferred"/>: nó là MỘT NHÁNH trạng thái do cơ chế chọn (đã quá ngân sách
    /// hay chưa), không phải một câu dặn chung. Prompt nền vào mọi lượt nên một bản sao ở đó sẽ giục BA
    /// đóng nhóm ngay từ lượt thứ hai. Prompt nền chỉ giữ con trỏ tới khối này (mục "Ngân sách buổi phỏng
    /// vấn").
    /// </para>
    ///
    /// <para>
    /// <paramref name="groupsToClose"/> gọi tên các nhóm còn hở để model khỏi phải tự dò bản đồ — và để nó
    /// KHÔNG lôi hai nhóm không-hỏi (phân quyền, thông báo) vào thẻ hỏi (danh sách đã loại chúng). Nhãn nhóm ở đây dành cho
    /// model; lệnh cấm đọc nhãn ra màn hình của prompt nền vẫn nguyên.
    /// </para>
    /// </summary>
    public static string InterviewClosing(int userTurnCount, IReadOnlyList<string> groupsToClose)
        => InterviewClosingHeading + " — đã hết ngân sách hỏi đáp, ĐÓNG các phần còn hở\n"
            + $"Người dùng đã trả lời {userTurnCount} lượt (ngân sách phần hỏi đáp của một buổi: "
            + $"{InterviewPaceBudget.ClosingAfterUserTurns} lượt). Từ lượt này, việc của bạn là ĐÓNG các "
            + "nhóm còn hở bằng ít lượt nhất, không phải đào thêm:\n"
            + "- KHÔNG mở chủ đề mới, KHÔNG hỏi thêm chi tiết cho nhóm đã [RÕ], KHÔNG đề xuất ý tưởng mới.\n"
            + "- Các nhóm còn hở phải đóng bằng câu hỏi: "
            + string.Join("; ", groupsToClose.Select(g => "«" + g + "»")) + ".\n"
            + "- Gom các điểm còn hụt của những nhóm đó vào thẻ hỏi gộp `questions` (tối đa 4 câu một "
            + "lượt). Mỗi câu là một câu ĐÓNG về đúng MỘT điểm còn hụt: nêu phương án cụ thể bạn đề xuất từ "
            + "điều đã biết, kèm chip [\"Đồng ý\", \"Tôi muốn khác\"] — hoặc 2–3 phương án thay thế khi "
            + "bạn không đủ căn cứ để chọn một. Người dùng chốt TỪNG câu: không viết một câu kiểu \"mình "
            + "chốt các điểm còn lại như sau\" để xin một cái gật cho cả cụm.\n"
            + "- Câu của nhóm «Luồng ngoại lệ & trường hợp đặc biệt» vẫn phải đứng một mình.\n"
            + "- Nhóm còn [MỘT PHẦN] mà bạn không chỉ ra được điểm hụt cụ thể nào ⇒ đừng bịa câu hỏi: phát "
            + "lại NGẮN (một dòng) điều đã ghi nhận của nhóm đó rồi xin chốt bằng câu ĐÓNG [\"Đúng rồi, "
            + "chốt vậy\", \"Tôi muốn sửa lại\"] — mỗi nhóm đúng một lần, và các câu như vậy cũng gộp được "
            + "vào cùng một thẻ. Phần phát lại dài hơn một dòng thì câu đó đứng một mình.\n"
            + "- Nhóm nào ngữ cảnh còn mang khối \"ĐỂ CUỐI, đừng hỏi lẻ\" hay \"KHÔNG hỏi, chỉ ghi nhận\" "
            + "thì vẫn KHÔNG hỏi: hệ thống tự lo nhóm đó.\n"
            + "- `ready` vẫn chỉ `true` khi mọi dòng bản đồ đã [RÕ]/[KHÔNG ÁP DỤNG].";
}
