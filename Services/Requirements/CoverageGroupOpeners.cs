namespace ICOGenerator.Services.Requirements;

/// <summary>
/// CÂU MỞ ĐẦU của từng nhóm trong bản đồ bao phủ — câu mà <see cref="RequirementReadinessGate"/> dùng khi
/// nó phải tự đặt câu hỏi thay BA và bản đồ không cho nó mẩu <c>còn thiếu:</c> nào để bám.
///
/// <para>
/// <b>Vì sao phải có bảng này.</b> Trước đây nhánh dự phòng của cổng phát MỘT câu duy nhất cho cả 12 nhóm:
/// *"Anh/chị kể giúp mình phần này trong công việc thực tế hiện đang diễn ra thế nào?"*. Câu đó không nói
/// được nhóm nào, không nói được đang hỏi cái gì, và trỏ tới *"phần này"* — đúng cụm THAM CHIẾU SUÔNG mà
/// <c>requirement-chat.v4.md</c> cấm BA dùng, vì người dùng chỉ thấy ô chat cuối trên màn hình. Ca thật ở
/// dự án JD Library, lượt 76: người dùng vừa trả lời xong một sự kiện thông báo thì nhận đúng câu đó và đáp
/// *"mình chưa hiểu câu hỏi, hãy hỏi rõ hơn"* — mất trắng một vòng ở cuối buổi phỏng vấn thứ 78. Cùng hình
/// dạng với vòng lặp câu hỏi chết mà <c>CoverageDeadQuestionLoopTests</c> đã chốt: nhánh này KHÔNG được
/// phép là một câu trống nghĩa, vì nó là lượt duy nhất người dùng nhìn thấy khi cổng chặn.
/// </para>
///
/// <para>
/// <b>Luật viết câu.</b> Hỏi bằng ngôn ngữ công việc của người dùng nghiệp vụ, KHÔNG dùng từ vựng nội bộ
/// của bản đồ ("vòng đời", "phạm vi", "đặc tả", "danh mục") ngay trong câu hỏi; kết thúc bằng dấu hỏi; và
/// hỏi ĐÚNG MỘT thứ — lượt chặn của cổng không có chip nào nên nó là câu MỞ, mà một câu mở nhiều vế thì
/// người dùng chỉ trả lời được vế cuối. Câu ở đây phải ĐỨNG MỘT MÌNH ĐƯỢC: cổng phát nó nguyên si, không
/// kèm câu dẫn nào nêu nhóm — nhãn nhóm không bao giờ lên màn hình nữa (xem
/// <see cref="RequirementReadinessGate"/>), nên một câu chỉ có nghĩa khi biết nó thuộc nhóm nào là một câu
/// hỏng. Ai muốn xem nhãn thì panel "Tiến độ khai thác" bên cạnh vẫn liệt kê đủ.
/// </para>
///
/// <para>
/// <b>Hai nhóm đọc khác hẳn</b> — «Thông báo / nhắc nhở» và «Phân quyền theo nghiệp vụ» là hai nhóm bị
/// prompt CẤM hỏi bằng câu hỏi (chúng được chốt bằng BẢNG ở cuối buổi, xem <see cref="InterviewTableGate"/>).
/// Phát cho chúng một câu hỏi khai thác là cổng tự phá đúng chốt chặn mà hai cái bảng sinh ra để dựng: một
/// danh sách vai trò trần trả lời cho cả nhóm. Vì vậy câu của hai nhóm này chỉ MỜI người dùng nhắn một
/// tiếng để phần đó được đưa ra rà — và cố tình không hứa hẹn "một cái bảng", vì dự án không có vòng đời
/// trạng thái nào thì bảng thông báo KHÔNG bao giờ được bày và nhóm quay về đường hỏi bằng câu hỏi
/// (<see cref="NotificationMapGate"/>). Cổng chỉ có bản đồ bao phủ trong tay nên nó không phân biệt được
/// hai ca đó; câu chữ vì thế phải đúng ở cả hai.
/// </para>
/// </summary>
public static class CoverageGroupOpeners
{
    // Khớp theo nhãn nhóm của requirement-coverage.v4.md. So khớp hai chiều bằng TIỀN TỐ (xem Find), nên
    // một dòng bản đồ viết ngắn hơn nhãn gốc ("Luồng ngoại lệ" thay vì "Luồng ngoại lệ & trường hợp đặc
    // biệt") vẫn tìm được câu của nó. CoverageGroupOpenersTests fail build nếu prompt thêm/đổi tên một nhóm
    // mà bảng này không theo — thiếu một câu ở đây là nhánh dự phòng lại rơi về một lượt trống nghĩa.
    private static readonly (string Label, string Question)[] Openers =
    {
        ("Mục tiêu / bài toán",
            "Ứng dụng này cần giải quyết việc gì cho anh/chị, và hiện tại việc đó đang vướng ở đâu?"),

        ("Đối tượng người dùng & vai trò",
            "Những ai sẽ dùng ứng dụng này, và mỗi người trong số họ làm việc gì trên đó?"),

        ("Chức năng & luồng nghiệp vụ chính",
            "Anh/chị kể giúp mình công việc này chạy từ đầu tới cuối thế nào: ai làm bước nào, và mỗi bước "
            + "xong thì ra được cái gì?"),

        ("Quy trình hiện tại & điểm khó",
            "Hiện anh/chị đang làm việc này bằng công cụ gì, và chỗ nào mất nhiều thời gian hoặc dễ sai nhất?"),

        ("Luồng ngoại lệ & trường hợp đặc biệt",
            "Khi có chuyện không suôn sẻ — bị từ chối, nhập sai, quá hạn, phải làm lại — thì hiện anh/chị xử "
            + "lý thế nào?"),

        ("Dữ liệu / danh mục chính",
            "Ứng dụng cần lưu sẵn những danh sách nào để mọi người chọn ra khi làm việc, và ai chịu trách "
            + "nhiệm cập nhật từng danh sách đó?"),

        ("Quy tắc nghiệp vụ & ràng buộc",
            "Có quy định nào mà ứng dụng phải tự kiểm và chặn lại nếu ai đó làm sai — giới hạn số lượng, hạn "
            + "mức, thời hạn, hay điều kiện phải đủ mới được làm bước tiếp?"),

        ("Vòng đời & trạng thái",
            "Từ lúc được tạo ra tới lúc xong việc, một hồ sơ trong ứng dụng này đi qua những bước nào, và cái "
            + "gì đẩy nó từ bước này sang bước kế tiếp?"),

        // Nhóm chốt bằng BẢNG — xem phần "Hai nhóm đọc khác hẳn" ở doc của class.
        ("Thông báo / nhắc nhở",
            "Còn việc chốt xem mỗi lần có việc gì xảy ra trong ứng dụng thì ai cần được báo — anh/chị "
            + "nhắn cho mình một tiếng để mình đưa phần đó ra rà cùng nhé?"),

        // KHÔNG hỏi kèm "ai là người xem" nữa: mỗi báo cáo là một MÀN HÌNH (xem ReportMapBuilder), nên quyền
        // xem của nó thuộc bảng phân quyền — hỏi ở đây là bắt người dùng trả lời hai lần cho cùng một điều,
        // và nó cũng phá luật "hỏi ĐÚNG MỘT thứ" ở đầu file (câu mở nhiều vế thì chỉ vế cuối được trả lời).
        ("Báo cáo / thống kê",
            "Cuối tháng hoặc cuối kỳ, anh/chị cần xem lại những con số hay danh sách tổng hợp nào từ ứng dụng, "
            + "và mỗi cái giúp anh/chị quyết định việc gì?"),

        // Nhóm chốt bằng BẢNG — xem phần "Hai nhóm đọc khác hẳn" ở doc của class.
        ("Phân quyền theo nghiệp vụ",
            "Còn việc chốt xem mỗi vai trò được xem và làm những gì trên từng màn hình — anh/chị nhắn "
            + "cho mình một tiếng để mình đưa phần đó ra rà cùng nhé?"),

        ("Quy mô sử dụng",
            "Áng chừng bao nhiêu người sẽ dùng ứng dụng này, và mỗi tháng phát sinh khoảng bao nhiêu hồ sơ?"),
    };

    /// <summary>
    /// Câu mở đầu của nhóm mang nhãn <paramref name="label"/>, hoặc <c>null</c> khi không nhãn nào khớp
    /// (model tự nghĩ ra một tên nhóm). Caller phải chịu được <c>null</c>: dựng một câu hỏi bịa cho một
    /// nhóm không tồn tại còn tệ hơn một câu dẫn chung, vì nó hỏi người dùng về một thứ không có trong
    /// checklist nào.
    /// </summary>
    public static string? Find(string? label)
    {
        var name = (label ?? string.Empty).Trim();
        if (name.Length == 0)
            return null;

        foreach (var (candidate, question) in Openers)
        {
            if (name.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                return question;
        }

        return null;
    }
}
