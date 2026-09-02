using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// "Triển vọng phỏng vấn" của MỘT dự án — chắt lọc từ hội thoại trong MỘT lời gọi LLM (thay vì ba) ba danh
/// sách bổ trợ cho bản đồ bao phủ (<see cref="RequirementCoverageService"/>):
///  • <b>OpenQuestions</b> — điểm còn mơ hồ/mâu thuẫn chưa chốt: TỒN ĐỌNG câu hỏi được nạp vào ngữ cảnh lượt
///    chat sau (<see cref="BAChatService"/>) để BA hỏi cho hết ngay trong khung chat, không hiện thành panel
///    bắt user tự đọc (mục được chốt thì tự rời danh sách ở lượt sau). Mỗi mục mang theo NHÓM của bản đồ
///    bao phủ mà nó thuộc về — đầu vào của <see cref="CoveragePendingGuard"/>; xem
///    <see cref="Canonicalize"/> cho chỗ nhãn ấy được chốt về đúng một trong 12 nhóm.
///  • <b>WorkedExamples</b> — các ví dụ tính thử người dùng ĐÃ xác nhận cho quy tắc định lượng; nguồn để bước
///    sinh AI Design Spec đúc thành "## 13. Worked Examples" và POC tự kiểm (window.pocWorkedExamples) đối
///    chiếu ĐỘC LẬP: kỳ vọng do user chốt (trong spec), giá trị do chính POC tính ra.
/// <para>
/// Cùng pattern gộp-lũy-tiến theo con trỏ lượt (<see cref="Project.InterviewOutlookHarvestedTurnCount"/>) và
/// <b>fail-open</b> như bản đồ bao phủ: lời gọi lỗi thì giữ bản cũ + con trỏ đứng yên, lượt sau gộp bù.
/// Gọi ở HẬU KỲ lượt chat (sau frame done) để không cộng vào độ chờ cảm nhận.
/// Cả hai cột lưu dạng JSON, đọc/ghi qua <see cref="InterviewOutlookParser"/>.
/// </para>
///
/// <para>
/// <b>PHẠM VI MÀN HÌNH đã tách khỏi lời gọi này</b> (<see cref="InterviewScopeService"/>): nó chỉ được tiêu
/// thụ lúc bảng màn hình được bày ra hỏi, nên đi theo nhịp "sau mỗi lượt chat" của hai danh sách trên là
/// trả token cho mọi lượt để phục vụ một hai lượt — và tệ hơn, là bơm phỏng đoán của đầu buổi vào một bảng
/// mà không tầng nào được phép bớt đi. Xem service đó cho nhịp mới.
/// </para>
/// </summary>
public class InterviewOutlookService
{
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly CoverageChecklist _checklist;

    public InterviewOutlookService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts, CoverageChecklist checklist)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _checklist = checklist;
    }

    /// <summary>
    /// Gộp các lượt chat mới (kể từ con trỏ) vào hai danh sách rồi trả bản vừa chắt.
    /// <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK — các cột + con trỏ ghi thẳng lên nó.
    /// </summary>
    public async Task<InterviewOutlook> UpdateAndLoadAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        var harvested = project.InterviewOutlookHarvestedTurnCount;

        var delta = await _db.AgentConversations
            .Where(c => c.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Skip(harvested)
            .ToListAsync(cancellationToken);

        if (delta.Count == 0)
            return Current(project);

        var updated = await DistillAsync(project, delta, ba, model, cancellationToken);
        if (updated != null)
        {
            updated.OpenQuestions = Canonicalize(updated.OpenQuestions).ToList();
            project.OpenQuestions = InterviewOutlookParser.SerializeOpenQuestions(updated.OpenQuestions);
            project.WorkedExamples = InterviewOutlookParser.SerializeWorkedExamples(updated.WorkedExamples);
            project.InterviewOutlookHarvestedTurnCount = harvested + delta.Count;
            await _db.SaveChangesAsync(cancellationToken);
            return updated;
        }
        // updated == null ⇒ gộp lỗi: fail-open, giữ bản cũ + con trỏ cũ.
        return Current(project);
    }

    /// <summary>
    /// Đọc hai danh sách hiện có của project (không gọi LLM).
    /// </summary>
    public static InterviewOutlook Current(Project project) => new()
    {
        OpenQuestions = InterviewOutlookParser.ParseOpenQuestions(project.OpenQuestions).ToList(),
        WorkedExamples = InterviewOutlookParser.ParseWorkedExamples(project.WorkedExamples).ToList(),
    };

    /// <summary>
    /// Chốt nhãn nhóm của từng mục tồn đọng về ĐÚNG một trong 12 nhóm của checklist bao phủ — ngay ở
    /// ĐƯỜNG GHI, trước khi danh sách được lưu.
    /// <para>
    /// <b>Vì sao ở đây chứ không ở chỗ đối chiếu.</b> Nhãn này là đầu vào của một chốt chặn tất định
    /// (<see cref="CoveragePendingGuard"/>) nhưng do model điền, nên nó lệch được theo đủ kiểu:
    /// *"Luồng ngoại lệ"* cho *"Luồng ngoại lệ &amp; trường hợp đặc biệt"*, hoặc một cái tên model tự
    /// nghĩ ra. Chuẩn hoá một lần ở đường ghi thì mọi tầng đọc sau đó thấy CÙNG một nhãn — đúng lý do
    /// <see cref="CoveragePendingGuard"/> chọn chạy ở đường ghi chứ không ở đường đọc. Nhãn không khớp
    /// nhóm nào ⇒ để RỖNG: guard bỏ qua mục không nhóm, tức fail-open y như khi thẻ cũ không parse được —
    /// mục vẫn nằm trong ngữ cảnh chat để BA hỏi, chỉ không hạ được dòng bản đồ nào.
    /// </para>
    /// Checklist rỗng (không bóc được từ prompt) ⇒ trả nguyên: không có gì để đối chiếu thì giữ lại nhãn
    /// model đưa còn hơn xoá trắng đầu vào của guard.
    /// </summary>
    private IReadOnlyList<OpenQuestionEntry> Canonicalize(IReadOnlyList<OpenQuestionEntry> items)
    {
        var labels = _checklist.Skeleton().Select(x => x.Label).Where(l => l.Length > 0).ToList();
        if (labels.Count == 0)
            return items;

        foreach (var item in items)
            item.Group = MatchLabel(labels, item.Group) ?? string.Empty;

        return items;
    }

    /// <summary>
    /// Nhãn checklist khớp với nhóm model viết ra. So khớp hai chiều bằng TIỀN TỐ, cùng luật với
    /// <c>CoveragePendingGuard.FindGap</c> và <c>InterviewTableGate.IsClear</c>; không khớp ⇒ null.
    /// </summary>
    private static string? MatchLabel(IReadOnlyList<string> labels, string? group)
    {
        group = (group ?? string.Empty).Trim();
        if (group.Length == 0)
            return null;

        return labels.FirstOrDefault(label =>
            label.StartsWith(group, StringComparison.OrdinalIgnoreCase)
            || group.StartsWith(label, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<InterviewOutlook?> DistillAsync(Project project, List<AgentConversation> turns, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Trạng thái hiện có (cập nhật cùng các lượt mới bên dưới; mục đã được chốt/giải quyết thì BỎ khỏi OpenQuestions)");
        // Echo lại trạng thái hiện có dạng bullet, KHÔNG dạng JSON: xem InterviewOutlookParser. Riêng
        // danh sách tồn đọng giữ nguyên thẻ nhóm ở đây — model cần thấy cặp nhóm↔câu hỏi để mục cũ không
        // bị nó gán lại sang nhóm khác ở lượt gộp này.
        var current = Current(project);
        sb.AppendLine("### Điểm cần làm rõ hiện có");
        sb.AppendLine(current.OpenQuestions.Count == 0 ? "(chưa có)" : InterviewOutlookParser.ToTaggedText(current.OpenQuestions));
        sb.AppendLine("### Ví dụ tính thử đã xác nhận hiện có");
        sb.AppendLine(current.WorkedExamples.Count == 0 ? "(chưa có)" : InterviewOutlookParser.ToText(current.WorkedExamples));
        sb.AppendLine();
        sb.AppendLine("## Các lượt hội thoại mới cần gộp");
        foreach (var t in turns)
            sb.AppendLine($"- {ConversationTurnRenderer.Render(t)}");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/interview-outlook.v2.md")),
            new(ChatRole.User, sb.ToString())
        };

        var (callResult, structured) = await _llm.ChatStructuredAsync<InterviewOutlook>(
            model, messages, ba.Temperature, new ModelCallLogContext(project.Id, ba, "BAInterviewOutlook"),
            cancellationToken: cancellationToken);

        if (!callResult.IsSuccess)
            return null;

        return structured ?? new InterviewOutlook();
    }
}
