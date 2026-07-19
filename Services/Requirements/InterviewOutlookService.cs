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
/// sách bổ trợ cho <see cref="DecisionLogService"/> ("Điều đã chốt"):
///  • <b>OpenQuestions</b> — điểm còn mơ hồ/mâu thuẫn chưa chốt: đối trọng với "Điều đã chốt", cho user thấy
///    chỗ tài liệu còn mỏng (mục được chốt thì tự rời danh sách ở lượt sau).
///  • <b>PlannedScope</b> — các màn hình/tính năng dự kiến, dựng dần để user bắt hiểu nhầm sớm ("Sẽ xây gì").
///  • <b>WorkedExamples</b> — các ví dụ tính thử người dùng ĐÃ xác nhận cho quy tắc định lượng; nguồn để bước
///    sinh AI Design Spec đúc thành "## 13. Worked Examples" và POC tự kiểm (window.pocWorkedExamples) đối
///    chiếu ĐỘC LẬP: kỳ vọng do user chốt (trong spec), giá trị do chính POC tính ra.
/// <para>
/// Cùng pattern gộp-lũy-tiến theo con trỏ lượt (<see cref="Project.InterviewOutlookHarvestedTurnCount"/>) và
/// <b>fail-open</b> như <see cref="DecisionLogService"/>: lời gọi lỗi thì giữ bản cũ + con trỏ đứng yên, lượt
/// sau gộp bù. Gọi ở HẬU KỲ lượt chat (sau frame done) để không cộng vào độ chờ cảm nhận.
/// </para>
/// </summary>
public class InterviewOutlookService
{
    private const int MaxCharsPerList = 4000;

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
    /// Gộp các lượt chat mới (kể từ con trỏ) vào ba danh sách rồi trả bản hiện hành.
    /// <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK — ba cột + con trỏ ghi thẳng lên nó.
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
            project.OpenQuestions = Store(updated.OpenQuestions);
            project.PlannedScope = Store(updated.PlannedScope);
            project.WorkedExamples = Store(updated.WorkedExamples);
            project.InterviewOutlookHarvestedTurnCount = harvested + delta.Count;
            await _db.SaveChangesAsync(cancellationToken);
            return updated;
        }
        // updated == null ⇒ gộp lỗi: fail-open, giữ bản cũ + con trỏ cũ.
        return Current(project);
    }

    /// <summary>Đọc ba danh sách hiện có của project (không gọi LLM) — cho query render trang.</summary>
    public static InterviewOutlook Current(Project project) => new()
    {
        OpenQuestions = ParseItems(project.OpenQuestions).ToList(),
        PlannedScope = ParseItems(project.PlannedScope).ToList(),
        WorkedExamples = ParseItems(project.WorkedExamples).ToList(),
    };

    /// <summary>Tách text bullet (mỗi dòng "- …") thành danh sách; rỗng → danh sách rỗng. Dùng chung với DecisionLog.</summary>
    public static IReadOnlyList<string> ParseItems(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();
        return text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => l[2..].Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static string? Store(List<string> items)
    {
        if (items.Count == 0)
            return null;
        var text = string.Join("\n", items.Select(i => "- " + i.Trim()));
        return text.Length > MaxCharsPerList ? text[..MaxCharsPerList] : text;
    }

    private async Task<InterviewOutlook?> DistillAsync(Project project, List<AgentConversation> turns, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Ba danh sách hiện có (cập nhật cùng các lượt mới bên dưới; mục đã được chốt/giải quyết thì BỎ khỏi OpenQuestions)");
        sb.AppendLine("### Điểm cần làm rõ hiện có");
        sb.AppendLine(string.IsNullOrWhiteSpace(project.OpenQuestions) ? "(chưa có)" : project.OpenQuestions.Trim());
        sb.AppendLine("### Màn hình/tính năng dự kiến hiện có");
        sb.AppendLine(string.IsNullOrWhiteSpace(project.PlannedScope) ? "(chưa có)" : project.PlannedScope.Trim());
        sb.AppendLine("### Ví dụ tính thử đã xác nhận hiện có");
        sb.AppendLine(string.IsNullOrWhiteSpace(project.WorkedExamples) ? "(chưa có)" : project.WorkedExamples.Trim());
        sb.AppendLine();
        sb.AppendLine("## Các lượt hội thoại mới cần gộp");
        foreach (var t in turns)
            sb.AppendLine($"- {ConversationTurnRenderer.Render(t)}");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/interview-outlook.v1.md")),
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
