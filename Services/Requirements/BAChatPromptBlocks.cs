using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Toàn bộ khối lệnh (system message) mà <see cref="BAChatService"/> lắp vào ngữ cảnh một lượt chat BA.
/// Đây chỉ là VĂN BẢN dựng từ dữ liệu đã chốt sẵn — không truy vấn, không gọi LLM, không quyết định gì:
/// việc chọn khối nào cho lượt nào là của <see cref="InterviewTableGate"/> và của chính
/// <see cref="BAChatService"/>.
///
/// <para>
/// Vì sao chúng ở đây chứ không nằm trong file prompt như <c>requirement-chat.v4.md</c>: các khối này
/// được dựng TỪ DỮ LIỆU của dự án (phạm vi màn hình, các dòng gieo của bảng thông báo, danh sách người
/// nhận) và phải đúng khớp với các builder đọc lại kết quả, nên chúng đi cùng code chứ không mở cho
/// Prompt Studio sửa. Phần luật viết thuần văn phong — cách hỏi, giọng, nhịp phỏng vấn — vẫn nằm ở file
/// prompt như cũ.
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
    public static string CoverageMap(string coverageMap)
        => "## Bản đồ bao phủ yêu cầu (trạng thái khai thác từng nhóm thông tin — dùng để chọn câu hỏi kế tiếp)\n"
            + "Nhóm đã [RÕ]: KHÔNG hỏi lại. Nhóm [MỘT PHẦN]: chỉ hỏi ĐÚNG phần ghi sau \"còn thiếu:\", "
            + "KHÔNG phát lại câu hỏi mở đầu của nhóm đó (người dùng đã trả lời phần còn lại rồi).\n"
            + CoverageMapParser.ToText(CoverageMapParser.Parse(coverageMap));

    // "Điều đã chốt" (DecisionLogService): các quyết định người dùng đã nói/đã xác nhận, gộp lũy tiến
    // qua MỌI lượt. Trước đây nhật ký này chỉ hiện thành panel cạnh khung chat để người dùng tự canh —
    // một nghịch lý, vì BA mới là bên đọc được cả hội thoại, còn người dùng thì phải vừa kể chuyện vừa
    // đối chiếu một danh sách 40 dòng. Nay nó đi thẳng vào ngữ cảnh của BA (panel đã gỡ) để BA soát
    // mâu thuẫn NGAY trong lượt, thay vì dồn hết cho cổng soát trước lúc soạn tài liệu — lúc đó người
    // dùng phải chọn A/B cho một câu họ nói từ lượt 3, đã nguội hẳn bối cảnh.
    //
    // Vì sao không để BA tự đọc lại transcript: các lượt cũ bị ConversationMemoryService NÉN thành bản
    // tóm tắt để tiết kiệm token, nên chi tiết đã chốt bị bào mòn đúng ở hội thoại dài — cũng chính là
    // lúc mâu thuẫn dễ xảy ra nhất. Nhật ký là bản đúc kết duy nhất sống sót qua việc nén đó.
    public static string SettledDecisions(IReadOnlyList<string> settledDecisions)
        => "## Điều đã chốt (các quyết định người dùng ĐÃ nói/đã xác nhận — đối chiếu trước khi hỏi tiếp)\n"
            + "TRƯỚC khi soạn câu hỏi kế tiếp, đối chiếu câu người dùng VỪA trả lời với danh sách này. "
            + "Nếu nó CHỌI với một điều đã chốt, lượt này PHẢI là lượt gỡ mâu thuẫn (xem mục \"Soát mâu "
            + "thuẫn với điều đã chốt\" trong hướng dẫn), KHÔNG hỏi sang nhóm khác. Không chọi nhau thì "
            + "coi đây là điều đã biết: KHÔNG hỏi lại, KHÔNG bắt người dùng xác nhận lần nữa.\n"
            + string.Join("\n", settledDecisions.Select(d => "- " + d));

    // "Điểm cần làm rõ" (InterviewOutlookService.OpenQuestions): tồn đọng các điểm còn mơ hồ/mâu thuẫn
    // chắt từ hội thoại. Bản đồ ở trên chỉ có độ phân giải theo NHÓM ("Quy tắc nghiệp vụ: MỘT PHẦN"),
    // còn danh sách này giữ ĐÚNG điểm chưa chốt ("Reference Belt đồng bộ tự động hay nhập tay?") —
    // BA mỗi lượt chỉ hỏi 1-2 câu nên phần chưa hỏi tới cần một chỗ để không rơi. Trước đây danh sách
    // này chỉ hiện thành panel cạnh chat để user tự đọc; nay nó đi thẳng vào ngữ cảnh của BA — người
    // dùng chỉ cần trò chuyện, việc "hỏi cho hết" là của BA.
    public static string OpenQuestions(IReadOnlyList<string> openQuestions)
        => "## Điểm cần làm rõ còn tồn đọng (chắt từ các lượt trước — hỏi cho hết trong khung chat)\n"
            + "Chọn câu hỏi kế tiếp ƯU TIÊN từ danh sách này khi nó còn mục, trước khi mở nhóm mới trong "
            + "bản đồ bao phủ. Điểm nào người dùng đã trả lời ở lượt gần đây thì coi như xong, KHÔNG hỏi lại.\n"
            // Thẻ nhóm "[Vòng đời & trạng thái] …" bị GỠ trước khi vào ngữ cảnh: nó được gắn cho
            // CoveragePendingGuard đối chiếu tất định với bản đồ, không phải cho BA đọc ra. Nhãn nhóm là
            // từ vựng nội bộ của bản đồ và prompt chat cấm ném nó vào mặt người dùng nghiệp vụ — để
            // nguyên thì thẻ đi thẳng vào câu hỏi kế tiếp, đúng lỗi mà CoverageDeadQuestionLoopTests
            // đã phải dựng lưới một lần.
            + string.Join("\n", openQuestions.Select(q => "- " + CoveragePendingGuard.StripGroupTag(q)));

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
    public static string PermissionMatrixTable(IReadOnlyList<string> effectiveScreens)
        => "## LƯỢT NÀY: BÀY BẢNG PHÂN QUYỀN (bắt buộc)\n"
            + "Mọi nhóm khác của bản đồ bao phủ đã [RÕ]. Lượt này là lượt chốt nhóm «Phân quyền theo "
            + "nghiệp vụ», và nó được chốt bằng BẢNG chứ không bằng câu hỏi.\n"
            + "Trả về trường `permissionMatrix`: mỗi dòng là MỘT chức năng của MỘT màn hình, kèm quyền của "
            + "từng vai trò. Ràng buộc:\n"
            + "- `screen` phải CHÉP ĐÚNG một mục trong danh sách phạm vi bên dưới — không thêm màn hình mới, "
            + "không gộp hai mục làm một. Mục nào bạn không nêu, hệ thống tự bổ sung vào bảng ở trạng thái "
            + "chưa ai có quyền.\n"
            + "- `function`: động từ nghiệp vụ ngắn (\"Xem\", \"Tạo\", \"Sửa\", \"Xóa\", \"Duyệt/Từ chối\", "
            + "\"Cập nhật kết quả\"). Có khối \"Bảng màn hình đã được NGƯỜI DÙNG CHỐT\" trong ngữ cảnh thì "
            + "LẤY THEO danh sách chức năng của đúng màn hình đó — người dùng vừa tự tay tích từng chức "
            + "năng, nên tự nghĩ ra một danh sách khác là bắt họ phân quyền cho những việc chưa ai duyệt, "
            + "và bỏ sót một chức năng họ đã giữ thì chức năng ấy mặc nhiên thành \"không ai được làm\". "
            + "Chỉ thêm chức năng ngoài danh sách khi chính hội thoại có nêu.\n"
            + "- `grants`: mỗi vai trò một mục, `scope` là MỘT trong \"của mình\" / \"của đơn vị\" / \"tất cả\", "
            + "hoặc để rỗng nếu vai đó không có quyền. Phạm vi là phần quan trọng nhất của bảng — "
            + "\"xem Training Plan\" và \"xem Training Plan do mình lập\" là hai yêu cầu khác hẳn nhau.\n"
            + "- `evidence`: CHỈ điền khi người dùng đã tự nói điều đó trong hội thoại, và điền đúng trích dẫn "
            + "của họ. Ô có trích dẫn được khóa lại như điều đã chốt; ô bạn suy đoán thì để trống trường này "
            + "và người dùng sẽ tự chọn. TUYỆT ĐỐI không bịa trích dẫn để ô trông như đã chốt.\n"
            + "- `condition`: điều kiện dữ liệu mà bốn nấc phạm vi không chở nổi (\"chỉ đăng ký được khóa nằm "
            + "trong danh sách bắt buộc của mình\", \"chỉ sửa khi chưa submit\"). Không có thì để rỗng.\n"
            + "`message` chỉ là MỘT câu ngắn mời người dùng rà bảng rồi bấm \"Gửi bảng phân quyền\" — không đặt "
            + "câu hỏi, không kèm `suggestions`, không kèm `questions`: bảng là chỗ "
            + "trả lời DUY NHẤT của lượt này.\n\n"
            + "### Phạm vi dự kiến (mỗi mục là MỘT dòng nhóm của bảng — chép nguyên văn vào `screen`)\n"
            + string.Join("\n", effectiveScreens.Select(s => "- " + s));

    /// <summary>Nhóm phân quyền chưa tới lượt chốt: cấm hỏi lẻ, nhưng nói rõ phần nào VẪN phải hỏi.</summary>
    public const string PermissionMatrixDeferred =
        "## Nhóm «Phân quyền theo nghiệp vụ» — ĐỂ CUỐI, đừng hỏi lẻ\n"
            + "KHÔNG hỏi các câu kiểu \"mỗi vai trò được xem và thao tác những gì\", \"vai X còn được làm gì "
            + "nữa không\", và KHÔNG tự soạn một phương án phân quyền rồi xin người dùng gật. Quyền xem/tạo/"
            + "sửa/xóa theo từng màn hình sẽ được chốt bằng MỘT BẢNG ở cuối buổi, khi phạm vi màn hình đã "
            + "đứng yên — hỏi bây giờ chỉ nhận về \"cứ vậy đã, có gì tôi bổ sung sau\".\n"
            + "Vẫn PHẢI hỏi như thường: vai trò nào làm bước nào trong LUỒNG (ai gửi, ai duyệt, ai bị từ "
            + "chối thì làm gì), vì câu trả lời đó đổi luôn câu hỏi kế tiếp của bạn; và ai QUẢN LÝ từng danh "
            + "mục dữ liệu. Đó là nhóm «Chức năng & luồng nghiệp vụ chính» và «Dữ liệu / danh mục chính», "
            + "không phải nhóm phân quyền.";

    public const string FlowMapTable =
        "## LƯỢT NÀY: BÀY BẢNG LUỒNG NGHIỆP VỤ (bắt buộc)\n"
            + "Các luồng chính đã rõ trong hội thoại. Lượt này bạn ráp chúng lại thành BẢNG để người dùng rà "
            + "từng bước — họ chưa bao giờ nhìn thấy bản bạn ráp, mà chính bản đó mới là thứ đi vào tài liệu.\n"
            + "Trả về trường `flowMap`: mỗi phần tử là MỘT luồng. Ràng buộc:\n"
            + "- `name`: tên luồng theo ngôn ngữ nghiệp vụ (\"Đăng ký khóa học\", \"Duyệt kế hoạch quý\").\n"
            + "- `kind`: \"luồng chính\" hoặc \"ngoại lệ\". PHẢI có ít nhất MỘT ngoại lệ nếu hội thoại có nhắc "
            + "tới bất kỳ đường hỏng nào (từ chối, quá hạn, trùng, thiếu điều kiện). Ngoại lệ là phần người "
            + "dùng không bao giờ tự kể — họ coi nó là hiển nhiên — nên đây là chỗ rẻ nhất để hỏi.\n"
            + "- `role`: vai trò khởi xướng luồng. `trigger`: CHỈ với ngoại lệ — điều kiện làm nó xảy ra.\n"
            + "- `steps`: từ 2 tới 10 bước theo đúng thứ tự, mỗi bước `{actor, action, outcome}`. `actor` là "
            + "vai làm bước đó; `outcome` là trạng thái/kết quả sau bước (để rỗng nếu bước không đổi trạng "
            + "thái). Luồng một bước KHÔNG phải luồng — hệ thống sẽ loại nó.\n"
            + "- `evidence` của TỪNG BƯỚC: CHỈ điền khi người dùng đã tự nói đúng bước đó, và điền đúng trích "
            + "dẫn của họ. Bước có trích dẫn được khóa lại như điều đã chốt; bước bạn suy ra thì để trống "
            + "trường này và người dùng sẽ tự soát. TUYỆT ĐỐI không bịa trích dẫn.\n"
            + "- CHỈ mô tả điều người dùng ĐÃ nói/đã chốt. Không thêm bước \"cho đủ quy trình\".\n"
            + "`message` chỉ là MỘT câu ngắn mời người dùng rà bảng rồi bấm \"Gửi bảng luồng\" — không đặt câu "
            + "hỏi, không kèm `suggestions`, không kèm `questions`: bảng là chỗ trả "
            + "lời DUY NHẤT của lượt này.";

    // Hai câu mở đầu khác nhau theo lượt bày đầu / bày lại. Nói "người dùng chưa bao giờ nhìn thấy
    // danh sách này" với một bảng họ vừa tự tay rà là sai sự thật, và model đọc câu đó sẽ mô tả lại
    // từ đầu cả những màn hình đã duyệt — đúng phần mà SeedRows sẽ bỏ đi, tức một lượt gọi tốn công
    // cho không.
    public static string ScreenScopeTable(
        bool reshow,
        IReadOnlyList<string> effectiveScreens,
        IReadOnlyList<string> pendingScreens,
        IReadOnlyList<string> pendingFunctions,
        string? flowMapJson)
    {
        var screenScopeIntro = reshow
            ? "## LƯỢT NÀY: BỔ SUNG BẢNG MÀN HÌNH ĐÃ CHỐT (bắt buộc)\n"
                + "Người dùng đã tự tay rà và CHỐT bảng màn hình ở một lượt trước. Sau đó hội thoại lộ "
                + "thêm phần MỚI, và lượt này bày lại bảng chỉ để lấy phần mới đó. Hệ thống giữ "
                + "nguyên các dòng người dùng đã duyệt, nên bạn CHỈ mô tả các mục ở phần \"MỚI\" cuối "
                + "khối này — mô tả lại màn hình đã có là công bỏ đi, và câu dẫn của lượt do "
                + "hệ thống soạn nên đừng nhắc tới chúng.\n"
            : "## LƯỢT NÀY: BÀY BẢNG MÀN HÌNH (bắt buộc)\n"
                + "Lượt này chốt PHẠM VI MÀN HÌNH của ứng dụng. Danh sách dưới đây được chắt ra từ hội "
                + "thoại nhưng người dùng chưa bao giờ nhìn thấy nó — mà mọi thứ phía sau (bảng phân "
                + "quyền, các màn hình của bản demo) đều đứng trên đúng danh sách này.\n"
                // Bảng này đứng SAU bảng đối tượng và bảng báo cáo đúng để hai loại màn hình ấy có mặt
                // ngay ở lần bày đầu. Model không được phép coi chúng là mục lạc: bỏ một màn hình quản
                // lý danh mục ra khỏi bảng là xoá một quyết định người dùng vừa tự tay chốt ở bảng
                // trước, và người dùng sẽ đọc bảng này như thể danh mục đó không cần màn hình nào.
                + "Phạm vi này đã GỒM CẢ các màn hình do hai bảng người dùng vừa chốt sinh ra: màn hình "
                + "quản lý từng danh mục mà ứng dụng tự quản lý (từ bảng đối tượng) và mỗi báo cáo còn "
                + "giữ (từ bảng báo cáo). Chúng là quyết định NGƯỜI DÙNG vừa chốt, không phải mục bạn "
                + "chắt ra — phải có dòng riêng, và phần `purpose`/`functions` viết đúng như một màn hình "
                + "quản lý danh mục (xem, thêm, sửa, bỏ) hoặc một màn hình báo cáo (xem, lọc, xuất).\n";
        return screenScopeIntro
            + "Trả về trường `screenScopeMap`: mỗi phần tử là MỘT MÀN HÌNH. Ràng buộc:\n"
            + "- `screen` phải CHÉP ĐÚNG một mục trong danh sách phạm vi bên dưới — không thêm màn hình mới, "
            + "không tự đặt tên khác, không dịch tên tiếng Anh sang tiếng Việt, không thêm chữ dẫn kiểu "
            + "\"Màn hình …\"/\"… Screen\" (tên màn hình là nhãn menu của bản demo, và tên ngắn thì phép "
            + "so khớp bù chỉ chạy khi bạn chép đúng). Mục nào bạn không nêu, hệ thống tự bổ sung vào bảng.\n"
            + "- MỘT DÒNG = MỘT MÀN HÌNH, không phải một tính năng và không phải một luồng. Danh sách phạm "
            + "vi bên dưới được chắt theo lượt nên hay lẫn cả ba loại: mục nào đọc lên là một CHỨC NĂNG "
            + "(\"Tính năng Generate Training Implement từ Training Plan Detail\", \"Chỉnh sửa số lượng "
            + "lớp\") hay một LUỒNG (\"Luồng đăng ký khóa học với trạng thái pending, enroll, waitlist\") "
            + "thì ĐỪNG dựng thành dòng riêng: đưa nó vào `functions` của màn hình thật sự chứa nó, và ghi "
            + "nguyên văn mục đó vào `covers` của dòng ấy. Không ghi vào `covers` thì hệ thống tưởng bạn "
            + "bỏ quên và bổ sung nó lại thành một dòng trắng.\n"
            + "- `purpose`: MỘT câu nói màn hình này để làm gì, theo góc nhìn người dùng nghiệp vụ.\n"
            + "- `functions`: các chức năng trên màn, MỖI CHỨC NĂNG MỘT PHẦN TỬ `{name, flowSteps, "
            + "evidence}` — người dùng tích/bỏ tích từng chức năng một, nên đừng gói nhiều việc vào một "
            + "`name` (\"Xem, Sửa và Gửi duyệt\" là ba chức năng, không phải một).\n"
            + "- `flowSteps` của TỪNG chức năng: các BƯỚC của bảng luồng đã chốt mà CHỨC NĂNG ĐÓ phụ trách "
            + "— chép phần `action` của bước. Đây là phần quan trọng nhất của bảng: MỌI bước trong danh "
            + "sách cuối khối này phải được ÍT NHẤT MỘT chức năng nhận, và hệ thống đối chiếu tất định "
            + "chỗ này. Chức năng tra cứu không nằm trong luồng nào thì để mảng rỗng.\n"
            + "- `evidence`: CHỈ điền khi người dùng đã tự nêu màn hình / chức năng đó, kèm đúng trích dẫn "
            + "của họ. Dòng có trích dẫn được tích sẵn kèm dấu ✓; phần bạn suy ra thì để trống trường này.\n"
            + "`message` chỉ là MỘT câu ngắn mời người dùng rà bảng rồi bấm \"Gửi bảng màn hình\" — không đặt "
            + "câu hỏi, không kèm `suggestions`, không kèm `questions`.\n\n"
            + "### Phạm vi dự kiến (mỗi mục phải hoặc thành MỘT dòng `screen`, hoặc nằm trong `covers` của "
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
            // BẢNG KÊ CÁC BƯỚC PHẢI PHỦ. Các bước này đã có trong ngữ cảnh qua khối bảng luồng đã chốt,
            // nhưng ở đó chúng là một câu chuyện để đọc, còn ở đây là một danh sách để ĐỐI CHIẾU — và
            // chỗ hỏng của lượt này luôn là chỗ nối chứ không phải chỗ hiểu. Ca thật (JD Library 2):
            // bảng luồng có bước "Xem danh sách nhân viên trực tiếp dưới quyền", bảng màn hình dựng ra
            // mười bảy màn không màn nào nhận nó, và người dùng nhận về một câu hỏi thay vì một bảng.
            // Bước còn sót lại sau khối này thì ScreenStepPlacementService xếp chỗ ở hậu kỳ; danh sách
            // đây là để phần lớn ca không phải đi tới đó.
            + FlowStepChecklist(flowMapJson);
    }

    public const string EntityMapTable =
        "## LƯỢT NÀY: BÀY BẢNG ĐỐI TƯỢNG NGHIỆP VỤ (bắt buộc)\n"
            + "Lượt này chốt các ĐỐI TƯỢNG mà ứng dụng lưu hồ sơ, thông tin cần lưu về chúng, và vòng đời "
            + "trạng thái kèm người nhận thông báo.\n"
            + "Trả về trường `entityMap`: mỗi phần tử là MỘT đối tượng. Ràng buộc:\n"
            + "- BA CỘT TÊN của bảng này — `entity`, `fields[].name`, `states[].state` — viết bằng TIẾNG "
            + "ANH, 1–3 từ, dạng HIỂN THỊ Title Case (\"Training Plan\", \"Effective Date\", \"Pending "
            + "HRBP Approval\"), KHÔNG phải dạng định danh (`effective_date`, `EmployeeID`). Chúng là thứ "
            + "chảy ra mô hình dữ liệu và ra nhãn trên bản demo. Mọi ô CÒN LẠI viết bằng tiếng Việt — "
            + "`description`, `meaning`, `entryCondition`, `sourceSystem`, `rule`, `options`, `evidence` và cả "
            + "`message`: người rà bảng là người nghiệp vụ. Từ vựng riêng của đơn vị (OrgUnit, HRBP, JD, PC "
            + "Level) giữ NGUYÊN VĂN, đừng dịch lại.\n"
            + "- `entity`: tên đối tượng (\"Training Plan\", \"Leave Request\") — TUYỆT ĐỐI không dùng từ "
            + "vựng kỹ thuật (table, entity, model, khóa chính, quan hệ 1-n). Các bảng sau phải chép ĐÚNG "
            + "chuỗi này, nên đừng đổi cách đặt tên giữa chừng.\n"
            + "- `fields`: các thông tin cần lưu, mỗi mục `{name, meaning, required, input, source, options, "
            + "sourceSystem, rule, sourceColumn}`. Không liệt kê id/khóa/ngày tạo kỹ thuật — người dùng không "
            + "quyết định chúng. `meaning` là câu tiếng Việt giải nghĩa cái tên tiếng Anh bên cạnh và KHÔNG "
            + "ĐƯỢC để trống: một tên tiếng Anh cạnh một ô mô tả trống để người dùng đối diện đúng một từ "
            + "ngoại ngữ trơ trọi. Chưa chắc nghĩa thì vẫn viết cách hiểu của bạn — họ sửa một dòng, còn để "
            + "trống là họ không có gì để sửa.\n"
            + "- Thông tin nào có `source: \"app\"` thì tên của nó còn thành MỘT MÀN HÌNH \"<tên> Catalog\" "
            + "trên sidebar bản demo — thêm một lý do nữa để nó là tiếng Anh và ngắn.\n"
            + "- HAI TRỤC của một thông tin, độc lập nhau: `input` = người dùng nhập thế nào (`text` mặc "
            + "định · `number` · `date` · `choice-one` chọn 1 giá trị · `choice-many` chọn nhiều giá trị · "
            + "`auto` ứng dụng tự sinh), `source` = danh sách lấy ở đâu và CHỈ có nghĩa với hai kiểu chọn "
            + "(`inline` vài giá trị cố định liệt kê ở `options` · `app` ứng dụng tự quản lý danh mục · "
            + "`external` lấy từ hệ thống khác, ghi tên vào `sourceSystem`). `rule` chỉ dành cho `auto` và "
            + "chở quy tắc sinh mã đúng như người dùng nói.\n"
            + "- Hai trục này theo ĐÚNG luật của `evidence`: chỉ rời mặc định khi hội thoại đã nói tới. Chưa "
            + "ai bàn ⇒ `input: \"text\"`, `source: \"\"` và để người dùng tự chọn trên bảng — đoán `app` là "
            + "âm thầm đặt hàng thêm một MÀN HÌNH cho dự án, đoán `external` là bịa ra một tích hợp không có "
            + "thật.\n"
            + "- `required` là *để trống có được không*, KHÁC ô tích \"cần lưu\" của bảng: chỉ bật cho thông "
            + "tin hội thoại đã nói rõ là bắt buộc, và luôn để `false` với `input: \"auto\"`.\n"
            + "- `states`: vòng đời, mỗi mục `{state, entryCondition}`. `entryCondition` là điều kiện "
            + "hoặc hành động đưa đối tượng vào trạng thái đó — lấy từ chính các bước của bảng luồng đã chốt. "
            + "KHÔNG nêu ai được báo ở mỗi trạng thái: đó là việc của bảng THÔNG BÁO ở cuối buổi, và mỗi "
            + "trạng thái ở đây sẽ thành một DÒNG của bảng đó. Đối tượng danh mục (phòng ban, khóa học) "
            + "KHÔNG có vòng đời — để mảng rỗng, đừng dựng ra trạng thái giả.\n"
            + "- `evidence`: CHỈ điền khi người dùng đã tự nêu đối tượng đó, kèm đúng trích dẫn của họ.\n"
            + "- Thông tin nào đã nằm trong \"Bảng cột … đã được NGƯỜI DÙNG CHỐT\" thì cứ đưa vào — hệ thống "
            + "tự đánh dấu nguồn; đừng hỏi lại ý nghĩa của chúng. Với đúng các thông tin đó, chép NGUYÊN VĂN "
            + "tên cột vào `sourceColumn` (\"Ngày hiệu lực\", \"Item Title\"): cột tên nay là tiếng Anh nên "
            + "hệ thống không tự nối lại được hai đầu, và mất mối nối ấy thì dòng mất dấu xuất xứ đúng ở chỗ "
            + "người dùng cần nhận ra thứ họ vừa tự tay tích. Thông tin không đến từ cột nào thì để rỗng — "
            + "tên không khớp cột đã tích nào sẽ bị hệ thống xoá.\n"
            + "- Một \"thông tin\" mà thật ra là NHIỀU DÒNG, mỗi dòng có hơn một thuộc tính (\"5 trách nhiệm, "
            + "mỗi cái kèm tỷ trọng %\", \"các dòng hàng của đơn\") thì TÁCH thành một phần tử `entityMap` "
            + "nữa: `parentEntity` chép ĐÚNG `entity` của dòng cha, `fields` là các cột của MỘT dòng, và "
            + "`minRows`/`maxRows` là số dòng mỗi cha (không ai nói thì để null). Tối đa MỘT cấp — đối "
            + "tượng đã có cha thì không được làm cha của đối tượng khác. Nhưng đừng tách khi mỗi mục chỉ "
            + "có ĐÚNG một giá trị (\"các kỹ năng yêu cầu\"): đó là một ô `choice-many`.\n"
            + "- BẢNG chốt CẤU TRÚC, không chốt RÀNG BUỘC. \"Tổng tỷ trọng phải bằng 100%\", \"luôn có một "
            + "dòng mặc định không sửa được\" là QUY TẮC — không ô nào ở đây chở chúng, và bạn hỏi chúng "
            + "bằng câu hỏi ở các lượt sau. Đừng nén chúng vào `meaning` hay `description`.\n"
            + "`message` chỉ là MỘT câu ngắn mời người dùng rà bảng rồi bấm \"Gửi bảng đối tượng\" — không đặt "
            + "câu hỏi, không kèm `suggestions`, không kèm `questions`.";

    // BÁO CÁO / THỐNG KÊ — bảng thứ ba. Khác các bảng kia ở chỗ nhóm của nó VẪN được hỏi bằng câu hỏi
    // suốt buổi (xem ReportMapGate): cổng chỉ mở khi nhóm đã [RÕ], nên tới lượt này BA đã có lời kể để
    // ráp thành các dòng. Không có vế đó thì bảng bày ra trống và người dùng phải tự chẻ câu chuyện của
    // họ thành bốn cột — ít hơn cả cái ô kể tự do mà bảng thay thế.
    public static string ReportMapTable(IReadOnlyList<string> entityNames)
        => "## LƯỢT NÀY: BÀY BẢNG BÁO CÁO / THỐNG KÊ (bắt buộc)\n"
            + "Lượt này chốt nhóm «Báo cáo / thống kê»: người dùng đã kể họ cần xem những con số/danh sách "
            + "tổng hợp nào, việc của bạn là ráp lại thành một danh sách có ranh giới để họ rà.\n"
            + "Trả về trường `reportMap`: mỗi phần tử là MỘT báo cáo. Ràng buộc:\n"
            + "- `report`: tên đọc được như MỘT MÀN HÌNH, viết bằng TIẾNG ANH 2–4 từ, thường có hậu tố "
            + "`Report`/`Dashboard` (\"Remaining Leave Report\") — mỗi dòng người dùng giữ lại sẽ thành một "
            + "màn hình thật của ứng dụng rồi thành nhãn mục menu của bản demo, nên tên tiếng Việt ở đây là "
            + "một nhãn tiếng Việt trên sidebar. Tên trống nghĩa (\"Thống kê\", \"Báo cáo tổng hợp\") thì "
            + "tới bảng màn hình không ai rà nổi nó. Ô `question` ngay dưới vẫn là tiếng Việt.\n"
            + "- `question`: báo cáo này TRẢ LỜI CÂU HỎI GÌ, viết bằng lời người dùng (\"để biết tháng này ai "
            + "chưa đi học\"). KHÔNG viết mô tả chức năng kiểu tài liệu (\"hiển thị danh sách có phân trang\") "
            + "— phần đó là việc của bước sinh spec, còn ô này là thứ chỉ người dùng mới biết.\n"
            + "- `source`: số liệu lấy từ ĐỐI TƯỢNG nào — chép đúng tên một đối tượng trong danh sách bên "
            + "dưới. Tên không khớp đối tượng nào sẽ bị hệ thống xoá khỏi ô, nên đừng bịa một nguồn mới.\n"
            + "- `breakdown`: gộp/lọc theo cái gì (kỳ báo cáo, đơn vị, trạng thái, người phụ trách…), ngăn "
            + "bằng dấu chấm phẩy. Đây là cột phân biệt một báo cáo thật với một bảng đổ dữ liệu ra màn "
            + "hình — chưa rõ thì để rỗng, đừng điền \"theo thời gian\" cho có.\n"
            + "- CHỈ nêu báo cáo mà hội thoại (hoặc tài liệu nguồn) đã nói tới. TUYỆT ĐỐI không rải thêm cho "
            + "đủ bộ: mỗi dòng thừa là một MÀN HÌNH mà người dùng chưa từng đặt hàng, và nó đi thẳng vào "
            + "phạm vi rồi vào bản demo. Cùng một câu hỏi nghiệp vụ xem theo tháng/quý/năm là MỘT dòng, kỳ "
            + "báo cáo ghi ở `breakdown`.\n"
            + "- KHÔNG có cột \"ai xem\": mỗi báo cáo là một màn hình nên quyền xem của nó sẽ được chốt ở bảng "
            + "phân quyền ngay sau đây, kèm cả phạm vi dữ liệu. Đừng nhét vai trò vào `question`.\n"
            + "`message` chỉ là MỘT câu ngắn mời người dùng rà bảng rồi bấm \"Gửi bảng báo cáo\" — không đặt "
            + "câu hỏi, không kèm `suggestions`, không kèm `questions`: bảng là chỗ "
            + "trả lời DUY NHẤT của lượt này.\n\n"
            + "### Các đối tượng đã chốt (chép NGUYÊN VĂN vào `source`)\n"
            + string.Join("\n", entityNames.Select(e => "- " + e));

    // THÔNG BÁO / NHẮC NHỞ — nhóm THỨ HAI không được hỏi bằng câu hỏi, và vì cùng lý do với nhóm phân
    // quyền: chuẩn [RÕ] đòi hai vế GHÉP ĐƯỢC với nhau (mỗi sự kiện biết người nhận của riêng nó) trong
    // khi câu hỏi tự nhiên lại tách chúng làm hai câu rời, rồi bốn chip vai trò đóng dấu [RÕ] cho cả
    // nhóm với nội dung "mọi thay đổi trạng thái gửi cho cả bốn nhóm". Bốn ca: đã chốt / lượt bày bảng
    // / còn phải chờ (cấm hỏi lẻ) / dự án không có vòng đời nào (không lệnh nào, nhóm quay về đường hỏi
    // bằng câu hỏi) — và ca nào cũng do CƠ CHẾ chọn, không để model tự đoán mình đang ở đâu.
    public static string NotificationMapTable(
        IReadOnlyList<NotificationMapRow> notificationSeedRows, IReadOnlyList<string> recipientOptions)
        => "## LƯỢT NÀY: BÀY BẢNG THÔNG BÁO (bắt buộc)\n"
            + "Đây là việc CUỐI CÙNG của buổi phỏng vấn: chốt nhóm «Thông báo / nhắc nhở», và nó được chốt "
            + "bằng BẢNG chứ không bằng câu hỏi.\n"
            + "Trả về trường `notificationMap`: mỗi dòng là MỘT sự kiện. Ràng buộc:\n"
            + "- `entity` + `event` phải CHÉP ĐÚNG một dòng trong danh sách sự kiện bên dưới (chúng là các "
            + "chuyển trạng thái người dùng vừa tự tay chốt ở bảng đối tượng). Dòng nào bạn không nêu, hệ "
            + "thống tự bổ sung vào bảng ở trạng thái chưa chọn người nhận.\n"
            + "- `to` và `cc` là MẢNG, mỗi phần tử phải CHÉP ĐÚNG NGUYÊN VĂN một mục trong danh sách người "
            + "nhận bên dưới. Giá trị không khớp mục nào sẽ bị bỏ. `cc` thường rỗng.\n"
            + "- CHỈ điền `to`/`cc` cho những sự kiện mà hội thoại ĐÃ nói ai nhận, và khi đó `evidence` là "
            + "đúng trích dẫn của người dùng. Sự kiện bạn chỉ suy đoán thì để `to`/`cc` RỖNG và không "
            + "`evidence` — người dùng sẽ tự chọn. TUYỆT ĐỐI không bịa trích dẫn, và TUYỆT ĐỐI không rải "
            + "người nhận cho đủ: mỗi mục thừa là một người nhận email mà không ai yêu cầu.\n"
            + "- Được thêm dòng NHẮC NHỞ ngoài danh sách (\"trước hạn 3 ngày\", \"quá hạn mà chưa ai duyệt\") "
            + "CHỈ khi người dùng đã tự nói tới nó — dòng thêm bắt buộc có `evidence`, không có thì hệ thống "
            + "bỏ. Ghi mốc thời gian vào `trigger`.\n"
            + "- Kênh gửi duy nhất của nền tảng là EMAIL nên KHÔNG hỏi và KHÔNG nêu kênh nào khác.\n"
            + "`message` chỉ là MỘT câu ngắn mời người dùng rà bảng rồi bấm \"Gửi bảng thông báo\" — không đặt "
            + "câu hỏi, không kèm `suggestions`, không kèm `questions`: bảng là chỗ "
            + "trả lời DUY NHẤT của lượt này.\n\n"
            + "### Các sự kiện (mỗi dòng là MỘT dòng của bảng — chép nguyên văn vào `entity` + `event`)\n"
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
    public const string NotificationDeferred =
        "## Nhóm «Thông báo / nhắc nhở» — ĐỂ CUỐI, đừng hỏi lẻ\n"
            + "KHÔNG hỏi các câu kiểu \"vai trò nào cần nhận email?\", \"sự kiện nào cần gửi thông báo?\", và "
            + "KHÔNG tự soạn một danh sách người nhận rồi xin người dùng gật. Nhóm này được chốt bằng MỘT "
            + "BẢNG ở cuối buổi (mỗi sự kiện một dòng, người nhận chọn từ danh sách) — hỏi bây giờ chỉ nhận "
            + "về một danh sách vai trò trần không gắn với sự kiện nào, và tài liệu sẽ đóng băng thành \"mọi "
            + "thay đổi trạng thái gửi cho cả bốn nhóm\", tức mỗi lần một bản ghi đổi trạng thái là cả nhà "
            + "máy nhận email.\n"
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
}
