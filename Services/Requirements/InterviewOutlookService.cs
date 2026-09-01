using System.Text;
using System.Text.Json;
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
///    bắt user tự đọc (mục được chốt thì tự rời danh sách ở lượt sau).
///  • <b>ScopeAdditions</b> — phần PHẠM VI MỚI lộ ra ở các lượt vừa gộp: màn hình chưa có trong bảng màn
///    hình, hoặc chức năng mới trên một màn hình đã có. Nó KHÔNG được lưu thành một danh sách riêng mà
///    ghép thẳng vào <see cref="Project.ScreenScopeMap"/> ở trạng thái CHỜ DUYỆT
///    (<see cref="ScreenScopeMapBuilder.Merge"/>), rồi <see cref="ScreenScopeGate"/> bày bảng ra hỏi.
///    Vì là nguồn DÒNG của bảng nên mục ở đây CHỈ được là màn hình: một mục kiểu "Tính năng X từ màn Y" lọt
///    vào sẽ thành một dòng phân quyền và một màn hình của bản demo, trong khi nó vốn là một cái nút trên
///    màn Y — chỗ đúng của nó là trường <c>functions</c> của chính màn Y. Luật đó sống trong prompt
///    <c>interview-outlook.v1.md</c>; ở tầng bảng, thứ dọn nốt phần lọt lưới là
///    <see cref="ScreenScopeRow.Covers"/>.
///  • <b>WorkedExamples</b> — các ví dụ tính thử người dùng ĐÃ xác nhận cho quy tắc định lượng; nguồn để bước
///    sinh AI Design Spec đúc thành "## 13. Worked Examples" và POC tự kiểm (window.pocWorkedExamples) đối
///    chiếu ĐỘC LẬP: kỳ vọng do user chốt (trong spec), giá trị do chính POC tính ra.
/// <para>
/// Cùng pattern gộp-lũy-tiến theo con trỏ lượt (<see cref="Project.InterviewOutlookHarvestedTurnCount"/>) và
/// <b>fail-open</b> như bản đồ bao phủ: lời gọi lỗi thì giữ bản cũ + con trỏ đứng yên, lượt sau gộp bù.
/// Gọi ở HẬU KỲ lượt chat (sau frame done) để không cộng vào độ chờ cảm nhận.
/// </para>
///
/// <para>
/// <b>Vì sao phạm vi là DELTA chứ không phải một danh sách viết lại mỗi lượt.</b> Bản cũ giữ cả phạm vi
/// trong một cột riêng (<c>Project.PlannedScope</c>) mà lời gọi này ghi đè sau mỗi lượt chat, song song với
/// bảng màn hình người dùng đã rà. Hai danh sách nói về cùng một thứ nhưng không bao giờ bằng nhau — chỉ
/// cần model diễn đạt lại một mục là chúng lệch — nên mọi tầng sau phải sống chung với phần lệch đó: một
/// phép so tập hợp để đoán "màn hình mới", một đường ghi ngược sau lúc chốt, và một danh sách cho phép phải
/// đọc lại từ lượt hội thoại vì cột kia đã bị chính lời gọi này viết đè giữa lúc bày bảng và lúc bấm gửi.
/// Trả về ĐÚNG PHẦN MỚI thì không còn danh sách thứ hai để lệch, và lời gọi chạy sau lưng người dùng chỉ
/// còn quyền THÊM vào một bảng chờ duyệt — không quyền nào chạm được vào thứ họ đã quyết.
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
    /// Gộp các lượt chat mới (kể từ con trỏ) vào hai danh sách + bảng màn hình rồi trả bản vừa chắt.
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
            project.OpenQuestions = Store(updated.OpenQuestions);
            project.WorkedExamples = Store(updated.WorkedExamples);

            // Phần phạm vi mới đi thẳng vào BẢNG, không qua một cột trung gian nào. Merge trả null khi
            // không có gì mới — ca thường gặp nhất của mọi lượt chat — và lúc đó cột bảng không bị đụng
            // tới: một lượt chat không làm phát sinh màn hình nào thì không có lý do gì để ghi lại bảng.
            var merged = ScreenScopeMapBuilder.Merge(project.ScreenScopeMap, updated.ScopeAdditions);
            if (merged != null)
                project.ScreenScopeMap = JsonSerializer.Serialize(merged);

            project.InterviewOutlookHarvestedTurnCount = harvested + delta.Count;
            await _db.SaveChangesAsync(cancellationToken);
            return updated;
        }
        // updated == null ⇒ gộp lỗi: fail-open, giữ bản cũ + con trỏ cũ.
        return Current(project);
    }

    /// <summary>
    /// Đọc hai danh sách hiện có của project (không gọi LLM). <c>ScopeAdditions</c> luôn rỗng ở đây và đó là
    /// đúng: nó là DELTA của một lượt gộp, không phải một trạng thái đọc lại được — trạng thái phạm vi nằm
    /// ở <see cref="Project.ScreenScopeMap"/>.
    /// </summary>
    public static InterviewOutlook Current(Project project) => new()
    {
        OpenQuestions = ParseItems(project.OpenQuestions).ToList(),
        WorkedExamples = ParseItems(project.WorkedExamples).ToList(),
    };

    /// <summary>Tách text bullet (mỗi dòng "- …") thành danh sách; rỗng → danh sách rỗng.</summary>
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

    /// <summary>
    /// Đóng gói một danh sách thành đúng khuôn bullet mà <see cref="ParseItems"/> đọc lại được; rỗng → null.
    /// </summary>
    public static string? Store(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
            return null;
        var text = string.Join("\n", items.Select(i => "- " + i.Trim()));
        return text.Length > MaxCharsPerList ? text[..MaxCharsPerList] : text;
    }

    private async Task<InterviewOutlook?> DistillAsync(Project project, List<AgentConversation> turns, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Trạng thái hiện có (cập nhật cùng các lượt mới bên dưới; mục đã được chốt/giải quyết thì BỎ khỏi OpenQuestions)");
        sb.AppendLine("### Điểm cần làm rõ hiện có");
        sb.AppendLine(string.IsNullOrWhiteSpace(project.OpenQuestions) ? "(chưa có)" : project.OpenQuestions.Trim());
        // BẢNG MÀN HÌNH ĐANG CÓ, kể cả phần chưa ai rà: đây là thứ quyết định `scopeAdditions` rỗng hay
        // không, nên nó phải đầy đủ tới từng chức năng. Thiếu nó thì mỗi lượt model lại nhả ra chính những
        // màn hình đã có bằng chữ khác đi, và mỗi lần như thế là một lượt bày bảng người dùng không có việc
        // gì để làm.
        sb.AppendLine("### Bảng màn hình đang có (CHỈ nêu ở scopeAdditions thứ KHÔNG có trong đây)");
        sb.AppendLine(RenderScopeState(project.ScreenScopeMap));
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

    /// <summary>Bảng màn hình đang lưu, dạng phẳng để model đối chiếu: mỗi màn một dòng, chức năng đi kèm.</summary>
    private static string RenderScopeState(string? screenScopeJson)
    {
        var rows = ScreenScopeMapBuilder.Parse(screenScopeJson);
        if (rows.Count == 0)
            return "(chưa có màn hình nào)";

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            // Dòng người dùng đã BỎ TÍCH vẫn phải kể ra: model không biết nó tồn tại thì lượt nào cũng đề
            // xuất lại đúng màn hình ấy. Merge sẽ chặn (bia), nhưng mỗi lần chặn là một mục vô ích trong
            // output — và một mục vô ích lặp lại đủ lâu sẽ kéo theo cả những mục cạnh nó.
            var mark = row.Included ? string.Empty : " [người dùng đã LOẠI — không đề xuất lại]";
            var functions = row.Functions.Where(f => f.Included).Select(f => f.Name).ToList();
            sb.AppendLine($"- {row.Screen}{mark}"
                + (functions.Count > 0 ? $" | chức năng: {string.Join(", ", functions)}" : string.Empty));
        }

        return sb.ToString().TrimEnd();
    }
}
