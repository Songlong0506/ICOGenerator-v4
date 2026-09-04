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
/// đo được ở Prompt Evals, có <c>PromptKey</c> để lần vết phiên bản. Nửa DỮ LIỆU — phạm vi màn hình, các
/// đối tượng đã chốt, các dòng gieo của bảng thông báo, danh sách người nhận — dựng từ chính dữ liệu dự án
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
/// dặn chung mà là MỘT NHÁNH trạng thái của cổng — hai nhánh còn lại là khối "## LƯỢT NÀY: BÀY BẢNG …" và
/// khối "bảng ĐÃ CHỐT". Prompt nền vào mọi lượt nên một bản sao ở đó chọi thẳng với hai nhánh kia, và với
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

    // Bản đồ bao phủ (nếu có): la bàn để BA chọn câu hỏi kế tiếp — ưu tiên nhóm ★ chưa rõ, không hỏi
    // lại nhóm đã [RÕ]. Prompt requirement-chat.v4 hướng dẫn cách dùng heading này.
    // Bản đồ được LƯU dạng JSON nhưng nạp vào ngữ cảnh chat dạng 12 dòng bullet: BA đọc nó để chọn câu
    // hỏi, không để sửa nó, nên dấu ngoặc nhọn ở đây chỉ tốn token và mời model chép cú pháp JSON ra câu
    // trả lời cho người dùng. Xem CoverageMapParser.ToText.
    // Câu hỏi nằm ở CỘT KHÁC (Project.OpenQuestions) nên chúng được GẮN vào dòng trước khi dựng bullet —
    // thiếu bước đó thì mọi dòng [MỘT PHẦN] mất vế "còn thiếu:", và câu dặn ngay dưới trỏ vào khoảng không.
    public static string CoverageMap(string coverageMap, IReadOnlyList<OpenQuestionEntry> openQuestions)
        => "## Bản đồ bao phủ yêu cầu (trạng thái khai thác từng nhóm thông tin — dùng để chọn câu hỏi kế tiếp)\n"
            + "Nhóm đã [RÕ]: KHÔNG hỏi lại. Nhóm [MỘT PHẦN]: chỉ hỏi ĐÚNG phần ghi sau \"còn thiếu:\", "
            + "KHÔNG phát lại câu hỏi mở đầu của nhóm đó (người dùng đã trả lời phần còn lại rồi).\n"
            + CoverageMapParser.ToText(CoverageMapParser.AttachQuestions(
                CoverageMapParser.Parse(coverageMap), openQuestions));

    // "Điểm cần làm rõ" (InterviewOutlookService.OpenQuestions): tồn đọng các điểm còn mơ hồ/mâu thuẫn
    // chắt từ hội thoại. Bản đồ ở trên chỉ có độ phân giải theo NHÓM ("Quy tắc nghiệp vụ: MỘT PHẦN"),
    // còn danh sách này giữ ĐÚNG điểm chưa chốt ("Reference Belt đồng bộ tự động hay nhập tay?") —
    // BA mỗi lượt chỉ hỏi 1-2 câu nên phần chưa hỏi tới cần một chỗ để không rơi. Trước đây danh sách
    // này chỉ hiện thành panel cạnh chat để user tự đọc; nay nó đi thẳng vào ngữ cảnh của BA — người
    // dùng chỉ cần trò chuyện, việc "hỏi cho hết" là của BA.
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

    public const string ConfirmedScreenScope =
        "## Bảng màn hình người dùng ĐÃ CHỐT (phạm vi màn hình — coi như điều đã biết)\n"
        + "KHÔNG hỏi lại ứng dụng cần màn hình nào, và KHÔNG đề xuất thêm màn hình ngoài danh sách này trừ "
        + "khi chính người dùng nêu ra một nhu cầu mới.";

    // Lệnh "đừng hỏi lại" ở đây chỉ phủ CẤU TRÚC, và câu thứ hai phải nói rõ điều đó. Bảng chốt xong
    // không có nghĩa là mọi câu hỏi về mô hình dữ liệu đã hết: các RÀNG BUỘC trên một tập dòng con —
    // "tổng tỷ trọng phải bằng 100%", "luôn có một dòng mặc định không sửa được" — không có ô nào trong
    // bảng để đứng, nên nếu lệnh này bị hiểu rộng thì chúng vĩnh viễn không được hỏi và POC dựng ra một
    // biểu mẫu cộng lại không ra gì cả. Đây là ngoại lệ nằm ở câu lệnh, khác ba ngoại lệ ghi ngay tại
    // dòng của nó trong RenderConfirmedBlock (đối tượng rỗng ruột, ô chọn chưa rõ nguồn).
    public const string ConfirmedEntityMap =
        "## Bảng đối tượng nghiệp vụ người dùng ĐÃ CHỐT (coi như điều đã biết)\n"
        + "KHÔNG hỏi lại thông tin nào cần lưu hay các trạng thái đi qua. Bảng này chốt CẤU TRÚC, không "
        + "chốt RÀNG BUỘC: các quy tắc trên một tập dòng con (tổng tỷ trọng, dòng mặc định luôn có sẵn, "
        + "điều kiện được sửa) VẪN phải hỏi như mọi quy tắc nghiệp vụ khác.";

    public const string ConfirmedReportMap =
        "## Bảng báo cáo / thống kê người dùng ĐÃ CHỐT (coi như điều đã biết)\n"
        + "KHÔNG hỏi lại ứng dụng cần báo cáo nào, mỗi báo cáo lấy số từ đâu hay gộp theo gì, và KHÔNG đề "
        + "xuất lại báo cáo người dùng đã bỏ. Mỗi báo cáo còn giữ là MỘT MÀN HÌNH của ứng dụng.";

    public static string ConfirmedPermissionMatrix(string confirmedMatrix)
        => "## Bảng phân quyền người dùng ĐÃ CHỐT (tự tay chọn từng ô — coi như điều đã biết)\n"
            + "KHÔNG hỏi lại bất kỳ quyền nào dưới đây, KHÔNG bắt xác nhận lần nữa, và KHÔNG dựng yêu cầu "
            + "trái với bảng này.\n"
            + confirmedMatrix;

    // Cấm TUYỆT ĐỐI, không ngoại lệ: bất biến của bảng (xem NotificationMapBuilder) bảo đảm mọi dòng
    // đã lưu đều trả lời xong — hoặc "không gửi", hoặc có người nhận. Bản trước còn một ngoại lệ cho
    // các dòng để trống người nhận, và nó là đường dẫn tới đúng vòng hỏi lẻ mà cả cái bảng sinh ra
    // để thay thế: một bảng 8 dòng gửi đi với 7 dòng trống ⇒ 14 lượt chat để hỏi lại từng sự kiện.
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

    // PHÂN QUYỀN — một trong HAI nhóm không được hỏi bằng câu hỏi (nhóm kia là «Thông báo / nhắc nhở»,
    // khối của nó nằm ngay dưới). Xem PermissionMatrixGate cho lý do đầy đủ; tóm tắt: hỏi "mỗi vai trò được xem và làm những gì?" là bắt người dùng nghiệp vụ tự dựng cả
    // ma trận trong đầu, nên câu trả lời thật gần như luôn là "cứ vậy đã, có gì tôi bổ sung sau" — rồi
    // BA tự soạn phương án và một chip "Đồng ý" đóng dấu [RÕ] cho cả nhóm. Ba trạng thái, ba lệnh khác
    // nhau, và lệnh nào cũng do CƠ CHẾ chọn chứ không để model tự đoán đang ở trạng thái nào.
    /// <summary>Tên các phần của template nhiều hình dạng — xem <see cref="Section"/>.</summary>
    public const string FirstShapeSection = "shape:first";
    public const string ReshowShapeSection = "shape:reshow";
    public const string RulesSection = "rules";

    /// <summary>Khóa prompt của sáu khối "## LƯỢT NÀY: BÀY BẢNG …" — một file cho mỗi bảng.</summary>
    public const string FlowMapPromptKey = "BusinessAnalyst/table-flow-map.v1.md";
    public const string ScreenScopePromptKey = "BusinessAnalyst/table-screen-scope.v1.md";
    public const string EntityMapPromptKey = "BusinessAnalyst/table-entity-map.v1.md";
    public const string ReportMapPromptKey = "BusinessAnalyst/table-report-map.v1.md";
    public const string PermissionMatrixPromptKey = "BusinessAnalyst/table-permission-matrix.v1.md";
    public const string NotificationMapPromptKey = "BusinessAnalyst/table-notification-map.v1.md";

    public static string PermissionMatrixTable(string rules, IReadOnlyList<string> effectiveScreens)
        => rules
            + "\n\n### Phạm vi dự kiến (mỗi mục là MỘT dòng nhóm của bảng — chép nguyên văn vào `screen`)\n"
            + string.Join("\n", effectiveScreens.Select(s => "- " + s));

    /// <summary>
    /// Nhóm phân quyền chưa tới lượt chốt: cấm hỏi lẻ, nhưng nói rõ phần nào VẪN phải hỏi.
    ///
    /// <para>
    /// <b>Đây là bản DUY NHẤT của lệnh cấm, và nó ở code chứ không ở prompt nền.</b> Lệnh này là MỘT NHÁNH
    /// trạng thái của cổng, không phải một câu dặn chung: hai nhánh kia là khối "## LƯỢT NÀY: BÀY BẢNG …"
    /// và khối "bảng ĐÃ CHỐT" ngay dưới. Gộp nó vào <c>requirement-chat.v4.md</c> — vào MỌI lượt — là để lệnh
    /// cấm chọi thẳng với lệnh bày bảng ở đúng lượt cổng mở, và nói "sẽ chốt ở cuối buổi" về một bảng người
    /// dùng vừa tự tay rà xong. Vị trí đính cũng là một phần của tác dụng: khối này đứng NGAY SAU bản đồ bao
    /// phủ, tức ngay sau dòng <c>Phân quyền theo nghiệp vụ: [CHƯA HỎI]</c> mà nó phải giải độc — xem
    /// <see cref="PermissionMatrixGate"/> cho lý do một câu dặn ở đầu prompt nền không đủ.
    /// </para>
    /// </summary>
    public const string PermissionMatrixDeferred =
        "## Nhóm «Phân quyền theo nghiệp vụ» — ĐỂ CUỐI, đừng hỏi lẻ\n"
            + "KHÔNG hỏi các câu kiểu \"mỗi vai trò được xem và thao tác những gì\", \"vai X còn được làm gì "
            + "nữa không\", và KHÔNG tự soạn một phương án phân quyền rồi xin người dùng gật. Quyền xem/tạo/"
            + "sửa/xóa theo từng màn hình sẽ được chốt bằng MỘT BẢNG ở cuối buổi, khi phạm vi màn hình đã "
            + "đứng yên — hỏi bây giờ chỉ nhận về \"cứ vậy đã, có gì tôi bổ sung sau\", rồi phương án bạn tự "
            + "viết được đóng dấu bằng một chip \"Đồng ý\" và cả nhóm coi như đã rõ trong khi không ai thật "
            + "sự trả lời.\n"
            + "Vẫn PHẢI hỏi như thường: vai trò nào làm bước nào trong LUỒNG (ai gửi, ai duyệt, ai bị từ "
            + "chối thì làm gì), vì câu trả lời đó đổi luôn câu hỏi kế tiếp của bạn nên hoãn là tự bịt mắt; "
            + "và ai QUẢN LÝ từng danh mục dữ liệu — trừ orgUnit và nhân sự (đã chốt: đồng bộ từ COMPAS). Đó "
            + "là nhóm «Chức năng & luồng nghiệp vụ chính» và «Dữ liệu / danh mục chính», không phải nhóm "
            + "phân quyền.";

    public static string FlowMapTable(string rules) => rules;

    // Bảng màn hình có HAI lời mở đầu loại trừ nhau, chọn bằng DỮ LIỆU chứ không để model tự đoán: nói
    // "người dùng chưa bao giờ nhìn thấy danh sách này" với một bảng họ vừa tự tay rà là sai sự thật, và
    // model đọc câu đó sẽ mô tả lại từ đầu cả những màn hình đã duyệt — đúng phần mà SeedRows sẽ bỏ đi,
    // tức một lượt gọi tốn công cho không. Cả hai lời mở đầu nằm chung MỘT file prompt với bộ luật dùng
    // chung; xem Section cho lý do không tách làm hai file.
    public static string ScreenScopeTable(
        string template,
        bool reshow,
        IReadOnlyList<string> effectiveScreens,
        IReadOnlyList<string> pendingScreens,
        IReadOnlyList<string> pendingFunctions,
        string? flowMapJson)
    {
        var intro = Section(template, reshow ? ReshowShapeSection : FirstShapeSection);
        var rules = Section(template, RulesSection);

        return (intro.Length > 0 ? intro + "\n\n" : string.Empty)
            + rules
            + "\n\n### Phạm vi dự kiến (mỗi mục phải hoặc thành MỘT dòng `screen`, hoặc nằm trong `covers` của "
            + "một dòng — không mục nào được bỏ rơi)\n"
            + string.Join("\n", effectiveScreens.Select(s => "- " + s))
            + (pendingScreens.Count > 0
            ? "\n\n### Màn hình MỚI (lộ ra SAU lúc người dùng chốt bảng — phần việc DUY NHẤT của lượt này)\n"
            + string.Join("\n", pendingScreens.Select(s => "- " + s))
            + "\nMục nào trong số này thật ra chỉ là một chức năng của màn hình ĐÃ CHỐT thì đừng "
            + "dựng thành dòng riêng: ghi nguyên văn nó vào `covers` của dòng ấy."
            : string.Empty)
            // Chức năng mới trên một màn hình ĐÃ CHỐT: hệ thống đã ghép sẵn nó vào dòng của màn hình ấy
            // và giữ nguyên phần còn lại, nên việc của model chỉ là ô "phục vụ bước nào". Không nói ra
            // thì model thấy một chức năng lạ trong bảng và mô tả lại cả màn hình quanh nó.
            + (pendingFunctions.Count > 0
            ? "\n\n### Chức năng MỚI trên màn hình đã chốt (đã có sẵn trong bảng, đừng dựng dòng mới)\n"
            + string.Join("\n", pendingFunctions.Select(s => "- " + s))
            : string.Empty)
            + FlowStepChecklist(flowMapJson);
    }

    public static string EntityMapTable(string rules) => rules;

    // BÁO CÁO / THỐNG KÊ — bảng thứ ba. Khác các bảng kia ở chỗ nhóm của nó VẪN được hỏi bằng câu hỏi
    // suốt buổi (xem ReportMapGate): cổng chỉ mở khi nhóm đã [RÕ], nên tới lượt này BA đã có lời kể để
    // ráp thành các dòng. Không có vế đó thì bảng bày ra trống và người dùng phải tự chẻ câu chuyện của
    // họ thành bốn cột — ít hơn cả cái ô kể tự do mà bảng thay thế.
    public static string ReportMapTable(string rules, IReadOnlyList<string> entityNames)
        => rules
            + "\n\n### Các đối tượng đã chốt (chép NGUYÊN VĂN vào `source`)\n"
            + string.Join("\n", entityNames.Select(e => "- " + e));

    // THÔNG BÁO / NHẮC NHỞ — nhóm THỨ HAI không được hỏi bằng câu hỏi, và vì cùng lý do với nhóm phân
    // quyền: chuẩn [RÕ] đòi hai vế GHÉP ĐƯỢC với nhau (mỗi sự kiện biết người nhận của riêng nó) trong
    // khi câu hỏi tự nhiên lại tách chúng làm hai câu rời, rồi bốn chip vai trò đóng dấu [RÕ] cho cả
    // nhóm với nội dung "mọi thay đổi trạng thái gửi cho cả bốn nhóm". Bốn ca: đã chốt / lượt bày bảng
    // / còn phải chờ (cấm hỏi lẻ) / dự án không có vòng đời nào (không lệnh nào, nhóm quay về đường hỏi
    // bằng câu hỏi) — và ca nào cũng do CƠ CHẾ chọn, không để model tự đoán mình đang ở đâu.
    public static string NotificationMapTable(
        string rules,
        IReadOnlyList<NotificationMapRow> notificationSeedRows,
        IReadOnlyList<string> recipientOptions)
        => rules
            + "\n\n### Các sự kiện (mỗi dòng là MỘT dòng của bảng — chép nguyên văn vào `entity` + `event`)\n"
            + string.Join("\n", notificationSeedRows.Select(r =>
            $"- entity: {r.Entity} | event: {r.Event}"
            + (string.IsNullOrWhiteSpace(r.Trigger) ? string.Empty : $" | khi: {r.Trigger}")))
            + "\n\n### Danh sách người nhận (chép NGUYÊN VĂN vào `to`/`cc`)\n"
            + "Đây là bảng \"Danh sách người nhận\" mà người dùng thấy ngay trên bảng thông báo và tự "
            + "thêm/sửa/xóa được. Bạn thì KHÔNG: mục bạn tự nghĩ ra sẽ bị hệ thống bỏ.\n"
            + string.Join("\n", recipientOptions.Select(o => "- " + o));

    // Không có dòng nào gieo được (dự án không có vòng đời trạng thái nào) VÀ buổi phỏng vấn đã tới cuối
    // ⇒ bảng này sẽ KHÔNG BAO GIỜ được bày, nên lệnh cấm phải tự tắt: giữ nó là khóa chết nhóm ở
    // [CHƯA HỎI] và nút "Write Requirement" không bao giờ sáng. Đây là đường thoát duy nhất của ca đó,
    // và nó khớp đúng điều kiện thứ ba của NotificationMapGate.
    //
    // Chính vế "tự tắt" ấy là lý do lệnh cấm KHÔNG được gộp vào requirement-chat.v4.md: prompt nền vào MỌI
    // lượt nên một bản sao ở đó cấm VÔ ĐIỀU KIỆN, và đường thoát chỉ tắt được một nửa — cơ chế gỡ khối này
    // ra trong khi prompt nền vẫn cấm hỏi. Bản sao ấy đã tồn tại (v4.md, mục 12 nhóm) và đã bắt đầu trôi
    // lệch; nay prompt nền chỉ còn con trỏ tới khối này, và InterviewTablePromptTests giữ cho nó không mọc
    // lại. Cùng lý do với PermissionMatrixDeferred — xem doc của hằng đó.
    public const string NotificationDeferred =
        "## Nhóm «Thông báo / nhắc nhở» — ĐỂ CUỐI, đừng hỏi lẻ\n"
            + "KHÔNG hỏi các câu kiểu \"vai trò nào cần nhận email?\", \"sự kiện nào cần gửi thông báo?\", và "
            + "KHÔNG tự soạn một danh sách người nhận rồi xin người dùng gật. Nhóm này được chốt bằng MỘT "
            + "BẢNG ở cuối buổi (mỗi sự kiện một dòng, người nhận chọn từ danh sách) — hỏi bây giờ là tách "
            + "AI NHẬN khỏi KHI NÀO GỬI thành hai câu rời, nên chỉ nhận về một danh sách vai trò trần không "
            + "gắn với sự kiện nào, và tài liệu sẽ đóng băng thành \"mọi thay đổi trạng thái gửi cho cả bốn "
            + "nhóm\", tức mỗi lần một bản ghi đổi trạng thái là cả nhà máy nhận email.\n"
            + "Vẫn PHẢI hỏi như thường: các TRẠNG THÁI một đối tượng đi qua và ĐIỀU KIỆN chuyển giữa chúng "
            + "(nhóm «Vòng đời & trạng thái») — đó là nguồn các dòng của bảng thông báo, không có nó thì "
            + "bảng ấy trống. Cũng KHÔNG hỏi về cấu hình email/SMTP: kênh gửi duy nhất đã chốt là email.";


    /// <summary>
    /// Danh sách các BƯỚC LUỒNG đã chốt, đính vào cuối khối lệnh bày bảng màn hình dưới dạng một bảng kê
    /// phải đối chiếu. Chưa chốt bảng luồng ⇒ chuỗi rỗng.
    ///
    /// <para>
    /// Các bước này đã nằm sẵn trong ngữ cảnh qua khối "bảng luồng đã chốt" của
    /// <c>FlowMapBuilder.RenderConfirmedBlock</c>, nhưng ở đó chúng là một câu chuyện để ĐỌC, kể theo từng
    /// luồng và trộn với vai trò, điều kiện kích hoạt, kết quả sau mỗi bước. Ở đây chúng là một danh sách
    /// phẳng để ĐỐI CHIẾU, đúng hình dạng mà <c>ScreenScopeMapBuilder.UncoveredActions</c> sẽ chấm ngay sau
    /// đó — và chỗ hỏng của lượt này chưa bao giờ là chỗ hiểu, nó là chỗ nối.
    /// </para>
    /// </summary>
    private static string FlowStepChecklist(string? flowMapJson)
    {
        var actions = FlowMapBuilder.IncludedActions(flowMapJson);
        if (actions.Count == 0)
            return string.Empty;

        return "\n\n### Các BƯỚC của bảng luồng đã chốt (mỗi bước phải có ÍT NHẤT MỘT chức năng nhận vào "
            + "`flowSteps` — hệ thống đối chiếu tất định danh sách này với bảng bạn trả về)\n"
            + string.Join("\n", actions.Select(a => "- " + a))
            + "\nBước nào không màn hình nào trong phạm vi trên làm được thì CỨ ĐỂ TRỐNG, đừng gán bừa vào "
            + "một màn cho đủ: gán sai là dựng một chức năng không có thật lên một màn hình có thật, và "
            + "người dùng đọc lướt qua nó như phần đã đúng. Hệ thống có một lượt riêng xử phần còn lại.";
    }

    /// <summary>
    /// Lấy MỘT phần của template nhiều hình dạng: phần thân nằm giữa dòng đánh dấu <c># {key}</c> và dòng
    /// đánh dấu kế tiếp. Chỉ dòng tiêu đề cấp 1 (<c>"# "</c>) là dấu phân phần; <c>"## "</c>/<c>"### "</c>
    /// trong thân là nội dung prompt bình thường.
    ///
    /// <para>
    /// Vì sao không tách làm hai file: bảng màn hình có hai LỜI MỞ ĐẦU loại trừ nhau (bày đầu / bày lại)
    /// nhưng dùng CHUNG một bộ luật trường. Hai file là chép bộ luật ấy ra hai chỗ rồi để chúng trôi lệch —
    /// đúng thứ lần tách prompt này dọn đi.
    /// </para>
    ///
    /// <para>
    /// <b>Fail-open cho bản sửa ở Prompt Studio.</b> Template không còn dòng đánh dấu nào (ai đó dán đè một
    /// bản phẳng) thì <see cref="RulesSection"/> trả về TRỌN template và các phần hình dạng trả về rỗng:
    /// model vẫn nhận đủ luật, chỉ mất phần chọn lời mở đầu. Mất luật mới là hỏng, mất lời mở đầu thì không.
    /// </para>
    /// </summary>
    public static string Section(string? template, string key)
    {
        var text = template ?? string.Empty;
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        if (!lines.Any(IsSectionMarker))
            return string.Equals(key, RulesSection, StringComparison.OrdinalIgnoreCase) ? text.Trim() : string.Empty;

        var body = new List<string>();
        var inside = false;
        foreach (var line in lines)
        {
            if (IsSectionMarker(line))
            {
                if (inside)
                    break;

                inside = string.Equals(line[2..].Trim(), key, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inside)
                body.Add(line);
        }

        return string.Join("\n", body).Trim();
    }

    private static bool IsSectionMarker(string line) => line.StartsWith("# ", StringComparison.Ordinal);
}
