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
/// Lượt SOẠN BẢNG PHÂN QUYỀN: khi <see cref="PermissionMatrixGate"/> mở (mọi nhóm khác đã <c>[RÕ]</c>, bản thiết
/// kế UX đã có), BA tự suy quyền của từng vai trên từng TÁC VỤ từ hội thoại và bảng luồng, rồi lưu
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
/// <b>Chạy ở ĐẦU lượt chat</b> (<c>BAChatService</c>), sau lượt thiết kế UX — các DÒNG của bảng là các tác vụ
/// của bản thiết kế, khoá theo ID tác vụ nên thiết kế lại màn hình không làm mất quyền nào. <b>Fail-open:</b> lời gọi lỗi, hoặc không ra được bảng nào có vai trò,
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
    /// Soạn bảng nếu đã tới lúc. Hai đường:
    /// <list type="bullet">
    ///   <item><b>Lần đầu</b> (<see cref="PermissionMatrixGate.ShouldDraft(Project)"/>) — cả bảng, mỗi tác vụ một dòng.
    ///   Trả <c>true</c> (caller nối câu báo "BA vừa soạn bảng phân quyền" và đóng dấu dòng bản đồ).</item>
    ///   <item><b>Bổ sung</b> (<see cref="PermissionMatrixGate.MissingTasks"/>) — chỉ các tác vụ mới lộ ra sau lần
    ///   đầu; mọi dòng đang lưu giữ nguyên. Trả <c>false</c>: không có gì mới để báo người dùng.</item>
    /// </list>
    /// <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK.
    /// </summary>
    public async Task<bool> UpdateAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        var first = PermissionMatrixGate.ShouldDraft(project);
        var missing = first ? new List<UxTask>() : PermissionMatrixGate.MissingTasks(project);
        if (!first && missing.Count == 0)
            return false;

        var blueprint = UxBlueprintBuilder.Parse(project.UxBlueprint);
        var turns = await _db.AgentConversations
            .AsNoTracking()
            .Where(c => c.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get(PromptKey)),
            new(ChatRole.User, RenderInput(project, blueprint, first ? null : missing, turns))
        };

        var (callResult, draft) = await _llm.ChatStructuredAsync<PermissionMatrixDraft>(
            model, messages, new ModelCallLogContext(project.Id, ba, first ? "BAPermissionMatrixDraft" : "BAPermissionMatrixPatch"),
            cancellationToken: cancellationToken);
        if (!callResult.IsSuccess)
            return false;

        // Builder là chốt chặn tất định: dòng = tác vụ của bản thiết kế (dòng trỏ vào tác vụ không có thật bị bỏ,
        // tác vụ bị quên vẫn có dòng ở trạng thái chưa ai có quyền), mọi dòng đủ mọi vai, và chỉ ô có TRÍCH DẪN
        // mới được khóa. Đường bổ sung giữ nguyên mọi dòng đang lưu (tham số keep).
        var existing = PermissionMatrixBuilder.Parse(project.PermissionMatrix);
        var rows = PermissionMatrixBuilder.Build(
            draft?.Rows, blueprint,
            roles: first ? null : PermissionMatrixBuilder.Roles(project.PermissionMatrix),
            keep: first ? null : existing);
        if (rows.Count == 0)
            return false;

        project.PermissionMatrix = JsonSerializer.Serialize(rows);
        await _db.SaveChangesAsync(cancellationToken);
        return first;
    }

    private static string RenderInput(Project project, UxBlueprint blueprint, IReadOnlyList<UxTask>? only, IReadOnlyList<AgentConversation> turns)
    {
        var sb = new StringBuilder();
        if (only == null)
        {
            sb.AppendLine("## Các tác vụ của ứng dụng (MỖI tác vụ là MỘT dòng của bảng — chép đúng ID vào `taskId`)");
            sb.AppendLine("Mỗi dòng: ID | màn hình đặt tác vụ | tác vụ [loại] · người làm theo luồng · bước luồng nó phụ trách");
            sb.AppendLine(UxBlueprintRenderer.RenderTasksForPermission(blueprint));
        }
        else
        {
            sb.AppendLine("## Các tác vụ MỚI cần thêm dòng (bảng đã có cho các tác vụ khác — CHỈ trả dòng cho các tác vụ dưới đây, chép đúng ID vào `taskId`)");
            sb.AppendLine(UxBlueprintRenderer.RenderTasksForPermission(blueprint, only));
            var current = PermissionMatrixBuilder.RenderConfirmedBlock(project.PermissionMatrix);
            if (!string.IsNullOrWhiteSpace(current))
            {
                sb.AppendLine();
                sb.AppendLine("## Bảng phân quyền đang có (giữ nhất quán với nó — cùng vai, cùng cách dùng phạm vi dữ liệu)");
                sb.AppendLine(current.Trim());
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
