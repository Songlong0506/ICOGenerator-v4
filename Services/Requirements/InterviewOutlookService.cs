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
/// "Triển vọng phỏng vấn" của MỘT dự án — chắt lọc từ hội thoại trong MỘT lời gọi LLM danh sách
/// <b>WorkedExamples</b>: các ví dụ tính thử người dùng ĐÃ xác nhận cho quy tắc định lượng; nguồn để bước
/// sinh AI Design Spec đúc thành "## 13. Worked Examples" và POC tự kiểm (window.pocWorkedExamples) đối
/// chiếu ĐỘC LẬP — kỳ vọng do user chốt (trong spec), giá trị do chính POC tính ra.
/// <para>
/// Cùng pattern gộp-lũy-tiến theo con trỏ lượt (<see cref="Project.InterviewOutlookHarvestedTurnCount"/>) và
/// <b>fail-open</b> như bản đồ bao phủ: lời gọi lỗi thì giữ bản cũ + con trỏ đứng yên, lượt sau gộp bù.
/// Gọi ở HẬU KỲ lượt chat (sau frame done) để không cộng vào độ chờ cảm nhận. Danh sách lưu dạng JSON,
/// đọc/ghi qua <see cref="InterviewOutlookParser"/>.
/// </para>
///
/// <para>
/// <b>HAI danh sách đã rời khỏi lời gọi này</b>, mỗi cái vì nhịp của nó không phải nhịp "sau mỗi lượt chat":
/// <list type="bullet">
///   <item>PHẠM VI MÀN HÌNH → <see cref="InterviewScopeService"/>: nó chỉ được tiêu thụ lúc bảng màn hình
///         được bày ra hỏi, nên đi theo nhịp này là trả token cho mọi lượt để phục vụ một hai lượt — và tệ
///         hơn, là bơm phỏng đoán của đầu buổi vào một bảng mà không tầng nào được phép bớt đi.</item>
///   <item>ĐIỂM CẦN LÀM RÕ → <see cref="RequirementCoverageService"/>, chạy TRONG lượt chat: danh sách câu
///         hỏi và bản đồ bao phủ ràng buộc nhau chặt tới mức chỉ đúng khi được viết cùng nhau (nhóm còn câu
///         hỏi MỞ ⇒ dòng không được <c>[RÕ]</c>). Chắt ở hậu kỳ như đây thì nó luôn cũ hơn bản đồ đúng một
///         lượt, và cổng "Write Requirement" bày ra câu hỏi người dùng vừa trả lời xong.</item>
/// </list>
/// Thứ ở lại đi theo nhịp ngược lại với cả hai: <c>WorkedExamples</c> không cần tươi trong lượt chat vì
/// không tầng nào của lượt chat đọc nó.
/// </para>
/// </summary>
public class InterviewOutlookService
{
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public InterviewOutlookService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>
    /// Gộp các lượt chat mới (kể từ con trỏ) vào danh sách rồi trả bản vừa chắt.
    /// <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK — cột + con trỏ ghi thẳng lên nó.
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
            project.WorkedExamples = InterviewOutlookParser.SerializeWorkedExamples(updated.WorkedExamples);
            project.InterviewOutlookHarvestedTurnCount = harvested + delta.Count;
            await _db.SaveChangesAsync(cancellationToken);
            return updated;
        }
        // updated == null ⇒ gộp lỗi: fail-open, giữ bản cũ + con trỏ cũ.
        return Current(project);
    }

    /// <summary>
    /// Đọc danh sách hiện có của project (không gọi LLM).
    /// </summary>
    public static InterviewOutlook Current(Project project) => new()
    {
        WorkedExamples = InterviewOutlookParser.ParseWorkedExamples(project.WorkedExamples).ToList(),
    };

    private async Task<InterviewOutlook?> DistillAsync(Project project, List<AgentConversation> turns, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Trạng thái hiện có (cập nhật cùng các lượt mới bên dưới)");
        // Echo lại trạng thái hiện có dạng bullet, KHÔNG dạng JSON: xem InterviewOutlookParser.
        var current = Current(project);
        sb.AppendLine("### Ví dụ tính thử đã xác nhận hiện có");
        sb.AppendLine(current.WorkedExamples.Count == 0 ? "(chưa có)" : InterviewOutlookParser.ToText(current.WorkedExamples));
        sb.AppendLine();
        sb.AppendLine("## Các lượt hội thoại mới cần gộp");
        foreach (var t in turns)
            sb.AppendLine($"- {ConversationTurnRenderer.Render(t)}");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/interview-outlook.v3.md")),
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
