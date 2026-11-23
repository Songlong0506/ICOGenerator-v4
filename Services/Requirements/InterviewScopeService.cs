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
/// Lượt chắt lọc PHẠM VI TRÔI: sau lần thiết kế UX đầu, đọc các lượt hội thoại MỚI và tìm các TÁC VỤ người dùng
/// vừa kể mà danh mục chưa có (một thao tác mới, một trạng thái mới, một danh mục mới cần quản lý). Chúng vào
/// <c>Project.UxBlueprint</c> ở trạng thái CHƯA XẾP CHỖ (<see cref="UxInventoryBuilder.AddPending"/>), và
/// <see cref="UxDesignGate"/> mở để lượt thiết kế kế tiếp đặt chúng vào màn hình.
///
/// <para>
/// <b>Chỉ chạy SAU lần thiết kế đầu.</b> Lượt thiết kế tự dựng danh mục từ TRỌN hội thoại, nên trước nó không
/// cần lượt chắt nào — và một lượt chắt chạy sớm chính là cái từng đẻ ra mười dòng phỏng đoán cho cùng một
/// thứ (<i>Course List</i>, <i>Course Catalog</i>, <i>Course Management</i>… ở dự án Safety Training 9).
/// </para>
///
/// <para>
/// <b>Nhịp theo LÔ</b> (<see cref="HarvestBatchThreshold"/>) — cùng khuôn với
/// <see cref="UserMemoryService.HarvestBatchThreshold"/>: một lượt chắt lọc chạy sau lưng người dùng thì cái
/// đắt là gọi nó quá thường, không phải gọi nó muộn. Con trỏ lượt là
/// <see cref="Project.InterviewScopeHarvestedTurnCount"/>, RIÊNG với con trỏ của bản đồ bao phủ; lượt thiết kế
/// đặt lại nó về cuối hội thoại vì nó vừa đọc hết. <b>Fail-open</b>: lời gọi lỗi thì giữ nguyên bản thiết kế và
/// con trỏ, lần sau gộp bù.
/// </para>
/// </summary>
public class InterviewScopeService
{
    public const string PromptKey = "BusinessAnalyst/interview-scope.v1.md";

    /// <summary>Số lượt hội thoại mới tối thiểu trước khi chắt lại sau lần thiết kế đầu.</summary>
    public const int HarvestBatchThreshold = 10;

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public InterviewScopeService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>Bản thuần dữ liệu của nhịp chắt lọc — để test và để gọi từ nơi không có entity.</summary>
    public static bool ShouldHarvest(string? coverageMap, string? flowMapJson,
        string? reportMapJson, string? blueprintJson, int harvestedTurns, int totalTurns)
    {
        if (totalTurns - harvestedTurns < HarvestBatchThreshold)
            return false;
        if (!UxBlueprintBuilder.IsDesigned(blueprintJson))
            return false;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return false;

        return UxDesignGate.PrecedingTablesDone(coverageMap, flowMapJson, reportMapJson)
               && InterviewTableGate.IsClear(items, CoverageGroupKey.MainFlow);
    }

    public static bool ShouldHarvest(Project project, int totalTurns)
        => ShouldHarvest(project.RequirementCoverageMap, project.FlowMap, project.ReportMap,
            project.UxBlueprint, project.InterviewScopeHarvestedTurnCount, totalTurns);

    /// <summary>Chắt nếu đã tới nhịp. Trả số tác vụ mới đã thêm. <paramref name="project"/> phải đang được track.</summary>
    public async Task<int> UpdateAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        var total = await _db.AgentConversations.CountAsync(c => c.ProjectId == project.Id, cancellationToken);
        if (!ShouldHarvest(project, total))
            return 0;

        var harvested = project.InterviewScopeHarvestedTurnCount;
        var delta = await _db.AgentConversations
            .Where(c => c.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Skip(harvested)
            .ToListAsync(cancellationToken);
        if (delta.Count == 0)
            return 0;

        var blueprint = UxBlueprintBuilder.Parse(project.UxBlueprint);
        var additions = await DistillAsync(project, blueprint, delta, ba, model, cancellationToken);
        if (additions == null)
            return 0; // lỗi gọi / trả rác ⇒ fail-open: giữ bản cũ + con trỏ cũ, lần sau gộp bù.

        var before = blueprint.Tasks.Count;
        var merged = UxInventoryBuilder.AddPending(blueprint, additions, project.FlowMap);
        if (merged != null)
            project.UxBlueprint = UxBlueprintBuilder.Serialize(merged);

        // Con trỏ vẫn tiến khi không có gì mới — nếu không, lô sau lại gộp đúng quãng vừa đọc.
        project.InterviewScopeHarvestedTurnCount = harvested + delta.Count;
        await _db.SaveChangesAsync(cancellationToken);
        return merged == null ? 0 : merged.Tasks.Count - before;
    }

    private async Task<List<UxTaskDraft>?> DistillAsync(Project project, UxBlueprint blueprint, List<AgentConversation> turns, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        // DANH MỤC ĐANG CÓ, đầy đủ tới từng tác vụ: đây là thứ quyết định lời đáp rỗng hay không. Thiếu nó thì
        // model nhả ra chính những tác vụ đã có bằng chữ khác, và mỗi mục như thế là một nút thứ hai cho cùng việc.
        sb.AppendLine("## Danh mục tác vụ ĐANG CÓ (CHỈ nêu thứ KHÔNG có trong đây)");
        sb.AppendLine(RenderState(blueprint));
        sb.AppendLine();
        sb.AppendLine("## Các lượt hội thoại cần gộp");
        foreach (var t in turns.Where(t => !ConversationTurnRenderer.IsFailureTurn(t)))
            sb.AppendLine($"- {ConversationTurnRenderer.Render(t)}");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get(PromptKey)),
            new(ChatRole.User, sb.ToString())
        };

        var (callResult, structured) = await _llm.ChatStructuredAsync<InterviewScope>(
            model, messages, new ModelCallLogContext(project.Id, ba, "BAInterviewScope"),
            cancellationToken: cancellationToken);

        if (!callResult.IsSuccess)
            return null;

        return structured?.TaskAdditions ?? new List<UxTaskDraft>();
    }

    private static string RenderState(UxBlueprint blueprint)
    {
        if (blueprint.Tasks.Count == 0)
            return "(chưa có tác vụ nào)";

        var sb = new StringBuilder();
        foreach (var obj in blueprint.Objects)
            sb.AppendLine($"* đối tượng: {obj.Name}" + (obj.States.Count > 0 ? $" · trạng thái: {string.Join(" → ", obj.States)}" : string.Empty));
        foreach (var task in blueprint.Tasks)
            sb.AppendLine($"- {task.Name}" + (task.Object.Length > 0 ? $" · đối tượng: {task.Object}" : string.Empty)
                + (task.Actors.Count > 0 ? $" · người làm: {string.Join(", ", task.Actors)}" : string.Empty));
        return sb.ToString().TrimEnd();
    }
}
