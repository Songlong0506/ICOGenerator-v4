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
/// Lượt SOẠN BẢNG PHÂN QUYỀN: khi <see cref="PermissionMatrixGate"/> mở (mọi nhóm khác đã <c>[RÕ]</c>, phạm vi
/// màn hình đã chốt), BA tự suy quyền của từng vai trên từng chức năng từ hội thoại và bảng luồng, rồi lưu
/// thẳng vào <see cref="Project.PermissionMatrix"/>. Nhóm «Phân quyền theo nghiệp vụ» lên <c>[RÕ]</c> theo
/// (<see cref="CoverageConfirmedTableGuard"/>), và người dùng xem/sửa bảng ở popup mở từ chính dòng đó trên
/// panel tiến độ.
///
/// <para>
/// <b>Vì sao không bày bảng cho người dùng chốt nữa.</b> Phần quyết định thật của nhóm này — vai nào làm
/// bước nào, ai quản lý danh mục nào, ai chỉ xem phần của mình — đã được hỏi trong hội thoại và chốt ở bảng
/// luồng. Phần còn lại (CRUD trên từng chức năng của từng màn) là hệ quả của những câu trả lời đó, và bắt
/// người dùng rà lại cả ma trận ở cuối buổi là hỏi lần hai. Ô nào hội thoại không cho căn cứ thì để trống
/// (prompt <c>permission-matrix-draft.v1.md</c>): một quyền rộng hơn thực tế đi thẳng vào tài liệu mà không
/// ai gõ ra nó là lỗi nặng hơn một ô trống người dùng có thể điền ở popup.
/// </para>
///
/// <para>
/// <b>Chạy ở ĐẦU lượt chat</b> (<c>BAChatService</c>), sau lượt soạn phạm vi màn hình — các DÒNG của bảng là
/// màn hình × chức năng của phạm vi đó. <b>Fail-open:</b> lời gọi lỗi, hoặc không ra được bảng nào có vai trò,
/// thì không ghi gì — cổng còn mở nên lượt kế soạn lại.
/// </para>
/// </summary>
public class PermissionMatrixDraftService
{
    public const string PromptKey = "BusinessAnalyst/permission-matrix-draft.v1.md";

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public PermissionMatrixDraftService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>
    /// Soạn và lưu bảng phân quyền nếu đã tới lúc. Trả true khi đã ghi bảng.
    /// <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK.
    /// </summary>
    public async Task<bool> UpdateAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        if (!PermissionMatrixGate.ShouldDraft(project))
            return false;

        var screens = PermissionMatrixGate.EffectiveScreens(project);

        var turns = await _db.AgentConversations
            .AsNoTracking()
            .Where(c => c.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get(PromptKey)),
            new(ChatRole.User, RenderInput(project, turns))
        };

        var (callResult, draft) = await _llm.ChatStructuredAsync<PermissionMatrixDraft>(
            model, messages, new ModelCallLogContext(project.Id, ba, "BAPermissionMatrixDraft"),
            cancellationToken: cancellationToken);
        if (!callResult.IsSuccess)
            return false;

        // Builder là chốt chặn tất định: dòng trỏ vào màn hình ngoài phạm vi bị bỏ, màn hình bị quên được bổ
        // sung ở trạng thái chưa ai có quyền, mọi dòng đủ mọi vai, và chỉ ô có TRÍCH DẪN mới được khóa.
        var rows = PermissionMatrixBuilder.Build(draft?.Rows, screens);
        if (rows.Count == 0)
            return false;

        project.PermissionMatrix = JsonSerializer.Serialize(rows);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Đầu vào: phạm vi màn hình đã chốt (từng chức năng kèm bước luồng nó phụ trách), bảng luồng đã chốt
    /// (ai làm bước nào), điều đã ghi nhận về vai trò, rồi hội thoại. Hai khối đầu là nguồn suy chính — prompt
    /// dạy "người làm bước có quyền trên chức năng phụ trách bước đó".
    /// </summary>
    private static string RenderInput(Project project, IReadOnlyList<AgentConversation> turns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Phạm vi màn hình đã chốt (mỗi màn hình là một nhóm dòng — chép nguyên văn vào `screen`)");
        foreach (var row in ScreenScopeMapBuilder.Parse(project.ScreenScopeMap).Where(r => r.Included))
        {
            sb.AppendLine($"* {row.Screen}");
            foreach (var function in row.Functions.Where(f => f.Included))
            {
                var steps = function.FlowSteps.Count > 0
                    ? $" (phục vụ bước: {string.Join("; ", function.FlowSteps)})"
                    : string.Empty;
                sb.AppendLine($"  - chức năng: {function.Name}{steps}");
            }
        }

        var flows = FlowMapBuilder.RenderConfirmedBlock(project.FlowMap);
        if (!string.IsNullOrWhiteSpace(flows))
        {
            sb.AppendLine();
            sb.AppendLine("## Bảng luồng nghiệp vụ người dùng đã chốt (ai làm bước nào)");
            sb.AppendLine(flows.Trim());
        }

        var roles = CoverageMapParser.Parse(project.RequirementCoverageMap)
            .FirstOrDefault(x => x.Key == CoverageGroupKey.Roles)?.Known ?? Array.Empty<string>();
        sb.AppendLine();
        sb.AppendLine("## Điều đã ghi nhận về «Đối tượng người dùng & vai trò»");
        sb.AppendLine(roles.Count > 0 ? string.Join("\n", roles.Select(k => "- " + k)) : "(chưa ghi gì)");

        sb.AppendLine();
        sb.AppendLine("## Hội thoại");
        // Lượt ⚠️ (lời gọi LLM lỗi) không chở yêu cầu nào — xem ConversationTurnRenderer.IsFailureTurn.
        foreach (var t in turns.Where(t => !ConversationTurnRenderer.IsFailureTurn(t)))
            sb.AppendLine($"- {ConversationTurnRenderer.Render(t)}");

        return sb.ToString();
    }
}
