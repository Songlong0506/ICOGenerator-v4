using System.ComponentModel;

namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Phán quyết GỘP của lượt trọng tài cho một lượt BA vừa soạn — thứ <c>BAChatService</c> nhận về. Đây là thứ
/// thay cho các bảng cụm từ tiếng Việt từng nằm ở <c>AskedQuestionHistory</c>, <c>BAChatReplyParser</c> và
/// <c>SourceRequestTurn</c>: "câu này hỏi rồi chưa", "chip này có chở câu trả lời không", "người dùng có nhắc
/// tới một file đang dùng không" đều là câu hỏi về NGHĨA, và nghĩa thì không đo được bằng <c>Contains</c> —
/// mỗi ngôn ngữ mới lại là một bảng nữa.
///
/// <para>
/// Gộp từ HAI lời gọi riêng (xem <c>InterviewJudge.JudgeTurnAsync</c>): <see cref="TurnRepeatVerdict"/> — phán
/// quyết khó, cần cả sổ "đã hỏi" — và <see cref="TurnSignalVerdict"/> — bốn phán quyết dễ, chỉ đọc lượt hiện
/// tại. Lớp này không phải kiểu structured output của lời gọi nào.
/// </para>
///
/// <para>
/// Mọi trường đều mặc định về phía "không phán gì": một phán quyết rỗng (lời gọi lỗi, model trả thiếu
/// trường) không xoá được thứ gì của lượt — xem <c>InterviewJudge</c> cho luật fail-open.
/// </para>
/// </summary>
public class TurnJudgeVerdict
{
    public List<int> Repeats { get; set; } = new();
    public List<TurnJudgeChips> EmptyChips { get; set; } = new();
    public List<int> YesNoChips { get; set; } = new();
    public bool UserMentionsExistingSource { get; set; }
    public bool AsksForFile { get; set; }

    /// <summary>Ghép hai nửa; nửa nào null (lời gọi bị bỏ qua hoặc hỏng) thì góp phần rỗng.</summary>
    public static TurnJudgeVerdict Merge(TurnRepeatVerdict? repeat, TurnSignalVerdict? signal) => new()
    {
        Repeats = repeat?.Repeats ?? new(),
        EmptyChips = signal?.EmptyChips ?? new(),
        YesNoChips = signal?.YesNoChips ?? new(),
        UserMentionsExistingSource = signal?.UserMentionsExistingSource ?? false,
        AsksForFile = signal?.AsksForFile ?? false
    };
}

/// <summary>
/// Phán quyết HỎI LẠI (prompt <c>BusinessAnalyst/turn-repeat-judge.v1.md</c>, mục đích <c>BATurnJudge</c>).
/// Phán quyết khó nhất của lượt — so NGHĨA với tới 80 câu đã hỏi — nên chạy trên model của agent BA.
/// </summary>
public class TurnRepeatVerdict
{
    [Description("SỐ THỨ TỰ (trong khối 'Câu hỏi của lượt BA cần xét') của những câu hỏi lại điều ĐÃ HỎI ở các lượt trước. Không câu nào ⇒ mảng rỗng.")]
    public List<int> Repeats { get; set; } = new();
}

/// <summary>
/// Bốn phán quyết DỄ của lượt (prompt <c>BusinessAnalyst/turn-signal-judge.v1.md</c>, mục đích
/// <c>BATurnSignalJudge</c>): chỉ đọc lượt BA và lượt người dùng hiện tại, không cần sổ "đã hỏi" — nên chạy
/// trên model của suất trọng tài (<c>ModelSlot.Judge</c>, màn AI Models) khi đã khai.
/// </summary>
public class TurnSignalVerdict
{
    [Description("Các chip KHÔNG chở câu trả lời nào ('Khác', 'Tự nhập', 'Other', 'Để tôi mô tả rõ hơn'...). Không có ⇒ mảng rỗng.")]
    public List<TurnJudgeChips> EmptyChips { get; set; } = new();

    [Description("SỐ THỨ TỰ của những câu hỏi mà bộ chip chỉ là MỘT CẶP có / không (một chip phủ nhận, một chip khẳng định). Không câu nào ⇒ mảng rỗng.")]
    public List<int> YesNoChips { get; set; } = new();

    [Description("true khi lượt người dùng mới nhất nhắc tới một file / bảng tính / biểu mẫu mà dữ liệu ĐANG nằm sẵn ở đó và có thể đính kèm được.")]
    public bool UserMentionsExistingSource { get; set; }

    [Description("true khi lượt BA cần xét là một lời NHỜ người dùng gửi / đính kèm file.")]
    public bool AsksForFile { get; set; }
}

/// <summary>Các chip rỗng của MỘT câu hỏi trong lượt BA.</summary>
public class TurnJudgeChips
{
    [Description("Số thứ tự của câu hỏi chứa các chip này.")]
    public int Question { get; set; }

    [Description("Chép NGUYÊN VĂN từng chip rỗng.")]
    public List<string> Chips { get; set; } = new();
}

/// <summary>
/// Phán quyết của lượt trọng tài cho danh sách CÂU HỎI TỒN ĐỌNG mà lượt chắt lọc bản đồ vừa viết ra
/// (prompt <c>BusinessAnalyst/coverage-question-judge.v1.md</c>). Thay cho <c>CoverageStaleGapGuard</c>
/// (đo "câu hỏi đã được trả lời chưa" bằng tỷ lệ từ trùng sau khi bỏ hư từ tiếng Việt) và hai bảng cụm của
/// <c>CoverageQuestionGuard</c> (câu hỏi rỗng, câu báo trạng thái).
/// </summary>
public class CoverageQuestionVerdict
{
    [Description("Những câu hỏi phải BỎ khỏi danh sách. Không có ⇒ mảng rỗng.")]
    public List<CoverageQuestionDrop> Drop { get; set; } = new();
}

/// <summary>Một câu hỏi tồn đọng bị trọng tài loại, kèm lý do để đọc lại được trong AI Call Logs.</summary>
public class CoverageQuestionDrop
{
    /// <summary>Phần đã ghi nhận của bản đồ đã trả lời câu này rồi.</summary>
    public const string Answered = "answered";

    /// <summary>Câu không nói được đang hỏi cái gì ("các quy tắc khác (nếu có)").</summary>
    public const string Hollow = "hollow";

    /// <summary>Câu tường thuật trạng thái của hệ thống thay vì hỏi người dùng.</summary>
    public const string NotAQuestion = "not-a-question";

    [Description("Số thứ tự của câu hỏi trong khối 'Câu hỏi tồn đọng'.")]
    public int Question { get; set; }

    [Description("Một trong: answered | hollow | not-a-question.")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Chỉ cho <see cref="Answered"/>: các mẩu đã ghi nhận mà trọng tài coi là câu trả lời, chép NGUYÊN VĂN
    /// từ bản đồ. Code đối chiếu từng mẩu với phần đã ghi nhận của những dòng được phép làm bằng chứng
    /// (chính nhóm của câu hỏi, hoặc một dòng <c>[RÕ]</c>) — không khớp, hoặc bỏ trống, thì phán quyết
    /// <c>answered</c> bị bỏ qua. Xem <c>CoverageQuestionGuard.IsGroundedAnswer</c>.
    /// </summary>
    [Description("CHỈ khi reason = answered: chép NGUYÊN VĂN (các) mẩu '·' đã ghi nhận trả lời TRỌN câu hỏi — lấy từ chính nhóm của câu hỏi hoặc từ một nhóm [RÕ]. Lý do khác ⇒ mảng rỗng.")]
    public List<string> Evidence { get; set; } = new();
}
